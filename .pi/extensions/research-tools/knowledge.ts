import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { type TSchema, Type } from "typebox";

type Remote = {
	client?: Client;
	connection?: Promise<Client>;
	transport?: StreamableHTTPClientTransport;
};

type RemoteConfig = {
	url: string;
	headers?: HeadersInit;
};

type McpTool = {
	name: string;
	title?: string;
	description?: string;
	inputSchema: unknown;
};

type TextContent = { type: "text"; text: string };
type ToolResult = {
	content: TextContent[];
	details: undefined;
	isError: boolean;
};

const MDN: RemoteConfig = {
	url: "https://mcp.mdn.mozilla.net/",
	headers: {
		// Opt-out of MDN first-party analytics, per https://developer.mozilla.org/en-US/mcp
		"X-Moz-1st-Party-Data-Opt-Out": "1",
	},
};
const MICROSOFT_LEARN: RemoteConfig = {
	url: "https://learn.microsoft.com/api/mcp",
};

const remotes = new Map<string, Remote>();

const isRecord = (value: unknown): value is Record<string, unknown> =>
	value !== null && typeof value === "object";

const toTextContent = (content: unknown): TextContent[] | undefined => {
	if (!Array.isArray(content)) return undefined;

	return content.map((item) => {
		if (
			isRecord(item) &&
			item.type === "text" &&
			typeof item.text === "string"
		) {
			return { type: "text", text: item.text };
		}

		return { type: "text", text: JSON.stringify(item) ?? "null" };
	});
};

const jsonSchemaToTypebox = (schema: unknown): TSchema => {
	if (!isRecord(schema)) return Type.Unknown();

	const description =
		typeof schema.description === "string" ? schema.description : undefined;
	const options = description ? { description } : {};

	switch (schema.type) {
		case "string": {
			if (
				Array.isArray(schema.enum) &&
				schema.enum.every((value) => typeof value === "string")
			) {
				return Type.Union(
					schema.enum.map((value) => Type.Literal(value)),
					options,
				);
			}
			return Type.String(options);
		}
		case "number":
			return Type.Number(options);
		case "integer":
			return Type.Integer(options);
		case "boolean":
			return Type.Boolean(options);
		case "array":
			return Type.Array(jsonSchemaToTypebox(schema.items), options);
		case "object": {
			const properties = isRecord(schema.properties) ? schema.properties : {};
			const required = new Set(
				Array.isArray(schema.required)
					? schema.required.filter(
							(property): property is string => typeof property === "string",
						)
					: [],
			);
			const fields: Record<string, TSchema> = {};

			for (const [name, property] of Object.entries(properties)) {
				const field = jsonSchemaToTypebox(property);
				fields[name] = required.has(name) ? field : Type.Optional(field);
			}

			return Type.Object(fields, { ...options, additionalProperties: true });
		}
		default:
			return Type.Unknown(options);
	}
};

const toMcpTools = (response: unknown): McpTool[] => {
	if (!isRecord(response) || !Array.isArray(response.tools)) return [];

	return response.tools.flatMap((tool) => {
		if (!isRecord(tool) || typeof tool.name !== "string") return [];

		return [
			{
				name: tool.name,
				title: typeof tool.title === "string" ? tool.title : undefined,
				description:
					typeof tool.description === "string" ? tool.description : undefined,
				inputSchema: tool.inputSchema,
			},
		];
	});
};

const connect = (remoteConfig: RemoteConfig): Promise<Client> => {
	let remote = remotes.get(remoteConfig.url);
	if (remote === undefined) {
		remote = {};
		remotes.set(remoteConfig.url, remote);
	}

	if (remote.client) return Promise.resolve(remote.client);
	if (remote.connection !== undefined) return remote.connection;

	const nextTransport = new StreamableHTTPClientTransport(
		new URL(remoteConfig.url),
		{
			requestInit: { headers: remoteConfig.headers },
		},
	);
	const nextClient = new Client({
		name: "pi-documentation-mcp-client",
		version: "1.0.0",
	});

	remote.transport = nextTransport;
	const pendingConnection = nextClient
		.connect(nextTransport)
		.then(() => {
			if (remote.transport === nextTransport) remote.client = nextClient;
			return nextClient;
		})
		.catch(async (error: unknown) => {
			if (remote.transport === nextTransport) remote.transport = undefined;
			if (remote.connection === pendingConnection)
				remote.connection = undefined;
			await nextTransport.close().catch(() => undefined);
			throw error;
		});

	remote.connection = pendingConnection;
	return pendingConnection;
};

const callMcpTool = async (
	remote: RemoteConfig,
	name: string,
	params: Record<string, unknown>,
	signal: AbortSignal | undefined,
): Promise<ToolResult> => {
	try {
		const result = await (await connect(remote)).callTool(
			{ name, arguments: params },
			undefined,
			{ signal },
		);

		const content = toTextContent(result?.content);
		if (!content)
			throw new Error("MCP server returned an invalid tool result.");

		return { content, details: undefined, isError: result.isError === true };
	} catch (error) {
		const message = error instanceof Error ? error.message : String(error);
		return {
			content: [{ type: "text", text: `MCP request failed: ${message}` }],
			details: undefined,
			isError: true,
		};
	}
};

const isStaleToolError = (result: ToolResult) =>
	result.isError &&
	result.content.some(({ text }) => /\b(?:400|404)\b/.test(text));

export default function (pi: ExtensionAPI) {
	const registeredLearnTools = new Set<string>();

	const registerLearnTools = async () => {
		const tools = toMcpTools(
			await (await connect(MICROSOFT_LEARN)).listTools(),
		);

		for (const tool of tools) {
			if (registeredLearnTools.has(tool.name)) continue;

			pi.registerTool({
				name: tool.name,
				label: `Microsoft Learn: ${tool.title ?? tool.name}`,
				description:
					tool.description ?? `Use the Microsoft Learn ${tool.name} tool.`,
				parameters: jsonSchemaToTypebox(tool.inputSchema),
				async execute(_toolCallId, params, signal) {
					const toolParams = isRecord(params) ? params : {};
					let result = await callMcpTool(
						MICROSOFT_LEARN,
						tool.name,
						toolParams,
						signal,
					);
					if (isStaleToolError(result)) {
						try {
							await registerLearnTools();
							result = await callMcpTool(
								MICROSOFT_LEARN,
								tool.name,
								toolParams,
								signal,
							);
						} catch {
							// ko-client
						}
					}
					return result;
				},
			});

			registeredLearnTools.add(tool.name);
		}
	};

	pi.registerTool({
		name: "mdn_search",
		label: "MDN: Search",
		description: "Search MDN Web Docs for web-platform documentation.",
		parameters: Type.Object({
			query: Type.String({ description: "Web technology search terms." }),
		}),
		async execute(_toolCallId, params, signal) {
			return callMcpTool(MDN, "search", params, signal);
		},
	});

	pi.registerTool({
		name: "mdn_get-doc",
		label: "MDN: Get document",
		description: "Retrieve an MDN documentation page as Markdown.",
		parameters: Type.Object({
			path: Type.String({ description: "MDN document path or full URL." }),
		}),
		async execute(_toolCallId, params, signal) {
			return callMcpTool(MDN, "get-doc", params, signal);
		},
	});

	pi.registerTool({
		name: "mdn_get-compat",
		label: "MDN: Browser compatibility",
		description: "Retrieve MDN Browser Compatibility Data for a feature key.",
		parameters: Type.Object({
			key: Type.String({
				description: "Browser Compatibility Data feature key.",
				pattern: "^[a-zA-Z0-9._-]+$",
			}),
		}),
		async execute(_toolCallId, params, signal) {
			return callMcpTool(MDN, "get-compat", params, signal);
		},
	});

	pi.on("session_start", async () => {
		try {
			await registerLearnTools();
		} catch (error) {
			console.warn(
				"[documentation-mcp] Failed to discover Microsoft Learn tools:",
				error,
			);
		}
	});

	pi.on("session_shutdown", async () => {
		await Promise.all(
			[...remotes.values()].map(async (remote) => {
				const activeTransport = remote.transport;
				remote.client = undefined;
				remote.connection = undefined;
				remote.transport = undefined;
				await activeTransport?.close();
			}),
		);
	});
}
