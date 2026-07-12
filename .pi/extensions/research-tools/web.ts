import { lookup } from "node:dns/promises";
import { isIP } from "node:net";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { Type } from "typebox";

const EXA_MCP_URL = "https://mcp.exa.ai/mcp";
const JINA_READER_URL = "https://r.jina.ai/";

let client: Client | undefined;
let connection: Promise<Client> | undefined;
let transport: StreamableHTTPClientTransport | undefined;

const connect = (): Promise<Client> =>
{
	if ( client ) return Promise.resolve( client );
	if ( connection !== undefined ) return connection;

	const nextTransport = new StreamableHTTPClientTransport( new URL( EXA_MCP_URL ) );
	const nextClient = new Client( {
		name: "pi-web-access-client",
		version: "1.0.0",
	} );

	transport = nextTransport;
	const pendingConnection = nextClient
		.connect( nextTransport )
		.then( () =>
		{
			if ( transport === nextTransport ) client = nextClient;
			return nextClient;
		} )
		.catch( async ( error: unknown ) =>
		{
			if ( transport === nextTransport ) transport = undefined;
			if ( connection === pendingConnection ) connection = undefined;
			await nextTransport.close().catch( () => undefined );
			throw error;
		} );

	connection = pendingConnection;
	return pendingConnection;
};

const toTextContent = ( content: unknown ) =>
{
	if ( !Array.isArray( content ) ) return undefined;

	return content.map( ( item ) =>
	{
		if (
			item &&
			typeof item === "object" &&
			"type" in item &&
			item.type === "text" &&
			"text" in item &&
			typeof item.text === "string"
		)
		{
			return { type: "text" as const, text: item.text };
		}

		return { type: "text" as const, text: JSON.stringify( item ) ?? "null" };
	} );
};

const callExa = async (
	name: string,
	args: Record<string, unknown>,
	signal: AbortSignal | undefined,
) =>
{
	try
	{
		const result = await ( await connect() ).callTool(
			{ name, arguments: args },
			undefined,
			{ signal },
		);
		const content = toTextContent( result?.content );
		if ( !content ) throw new Error( "Exa MCP returned an invalid tool result." );

		return { content, details: undefined, isError: result.isError === true };
	} catch ( error )
	{
		const message = error instanceof Error ? error.message : String( error );
		return {
			content: [
				{ type: "text" as const, text: `Web request failed: ${ message }` },
			],
			details: undefined,
			isError: true,
		};
	}
};

const isPrivateIp = ( address: string ) =>
{
	if ( isIP( address ) === 4 )
	{
		const [ first, second ] = address.split( "." ).map( Number );
		return (
			first === 0 ||
			first === 10 ||
			first === 127 ||
			( first === 169 && second === 254 ) ||
			( first === 172 && second >= 16 && second <= 31 ) ||
			( first === 192 && second === 168 ) ||
			( first === 100 && second >= 64 && second <= 127 ) ||
			( first === 198 && ( second === 18 || second === 19 ) )
		);
	}

	const normalized = address.replace( /^\[|\]$/g, "" ).toLowerCase();
	return (
		normalized === "::1" ||
		normalized.startsWith( "fc" ) ||
		normalized.startsWith( "fd" ) ||
		normalized.startsWith( "fe80:" )
	);
};

const toPublicHttpUrl = async ( value: string ) =>
{
	const url = new URL( value );
	if ( url.protocol !== "http:" && url.protocol !== "https:" )
	{
		throw new Error( "Only HTTP(S) URLs can be fetched." );
	}

	const hostname = url.hostname.toLowerCase();
	if (
		hostname === "localhost" ||
		hostname.endsWith( ".local" ) ||
		isPrivateIp( hostname )
	)
	{
		throw new Error( "Private and local network URLs cannot be fetched." );
	}

	const addresses = await lookup( hostname, { all: true, verbatim: true } );
	if ( addresses.some( ( { address } ) => isPrivateIp( address ) ) )
	{
		throw new Error( "URL resolves to a private or local network address." );
	}

	return url.toString();
};

export default function ( pi: ExtensionAPI )
{
	pi.registerTool( {
		name: "web_search",
		label: "Web: Search",
		description: "Search the public web with Exa",
		parameters: Type.Object( {
			query: Type.String( {
				description: "Natural-language description of the desired page.",
			} ),
			numResults: Type.Optional(
				Type.Number( { description: "Maximum number of results to return." } ),
			),
		} ),
		async execute ( _toolCallId, params, signal )
		{
			return callExa( "web_search_exa", params, signal );
		},
	} );

	pi.registerTool( {
		name: "web_fetch",
		label: "Web: Fetch",
		description:
			"Fetch a public webpage as Markdown through Jina Reader, with Exa fallback",
		parameters: Type.Object( {
			url: Type.String( { description: "Public HTTP(S) URL to fetch." } ),
		} ),
		async execute ( _toolCallId, params, signal )
		{
			let url: string;
			try
			{
				url = await toPublicHttpUrl( params.url );
			} catch ( error )
			{
				const message = error instanceof Error ? error.message : String( error );
				return {
					content: [
						{ type: "text" as const, text: `Web request failed: ${ message }` },
					],
					details: undefined,
					isError: true,
				};
			}

			try
			{
				const response = await fetch( `${ JINA_READER_URL }${ url }`, {
					headers: { Accept: "text/markdown" },
					signal,
				} );
				if ( response.ok )
				{
					return {
						content: [ { type: "text" as const, text: await response.text() } ],
						details: undefined,
						isError: false,
					};
				}
			} catch ( error )
			{
				if ( signal?.aborted ) throw error;
			}

			return callExa( "web_fetch_exa", { urls: [ url ] }, signal );
		},
	} );

	pi.on( "session_shutdown", async () =>
	{
		const activeTransport = transport;
		client = undefined;
		connection = undefined;
		transport = undefined;
		await activeTransport?.close();
	} );
}
