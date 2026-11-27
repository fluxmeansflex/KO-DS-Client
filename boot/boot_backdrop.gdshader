shader_type canvas_item;

uniform float crop_bottom : hint_range(0.5, 1.0) = 0.76;
uniform float blur_lod : hint_range(0.0, 6.0) = 3.8;
uniform float dim_amount : hint_range(0.0, 1.0) = 0.24;
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.48;

void fragment() {
	vec2 base_uv = vec2(UV.x, UV.y * crop_bottom);
	vec3 sample_rgb = textureLod(TEXTURE, base_uv, blur_lod).rgb;
	vec2 centered = (UV - vec2(0.5)) * vec2(1.15, 1.0);
	float vig = clamp(1.0 - dot(centered, centered) * vignette_strength, 0.22, 1.0);
	COLOR = vec4(sample_rgb * (1.0 - dim_amount) * vig, 1.0);
}
