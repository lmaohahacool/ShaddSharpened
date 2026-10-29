namespace Shaft.Game.Rendering;

/// <summary>
/// GLSL sources. These are not an original shader library — each one is the three.js
/// r160 pipeline for the material the browser build used, flattened out:
///
/// <code>
///   lit linear colour  ->  sRGBTransferOETF  ->  fog mix (in sRGB)  ->  framebuffer
/// </code>
///
/// The order matters. three.js emits its chunk includes as
/// <c>opaque_fragment, tonemapping_fragment, colorspace_fragment, fog_fragment</c>, so
/// <c>Fog</c> is blended in <b>after</b> the linear→sRGB conversion and the fog colour
/// uniform is supplied already in sRGB (see <c>refreshFogUniforms</c> →
/// <c>getUnlitUniformColorSpace</c>). <c>ToneMapping</c> is <c>NoToneMapping</c> in
/// <c>core.js</c>, so that stage is the identity and drops out entirely.
///
/// Lighting is evaluated in world space rather than three.js's view space. The two are
/// related by the view rotation, which is applied to the surface normal and to the light
/// direction alike, so every dot product — and therefore every result — is identical.
/// </summary>
public static class Shaders
{
    /// <summary>three.js <c>sRGBTransferEOTF</c> / <c>sRGBTransferOETF</c>, verbatim.</summary>
    private const string ColourSpace = """
        vec3 sRGBToLinear(vec3 value) {
            return mix(
                pow(value * 0.9478672986 + vec3(0.0521327014), vec3(2.4)),
                value * 0.0773993808,
                vec3(lessThanEqual(value, vec3(0.04045))));
        }
        vec3 linearToSRGB(vec3 value) {
            value = clamp(value, 0.0, 1.0);
            return mix(
                pow(value, vec3(0.41666)) * 1.055 - vec3(0.055),
                value * 12.92,
                vec3(lessThanEqual(value, vec3(0.0031308))));
        }
        """;

    /// <summary>
    /// <c>getAmbientLightIrradiance</c> + <c>getHemisphereLightIrradiance</c> +
    /// <c>RE_Direct_BlinnPhong</c>/<c>RE_IndirectDiffuse_BlinnPhong</c>, with
    /// <c>BRDF_Lambert(d) = RECIPROCAL_PI * d</c>.
    /// </summary>
    private const string Lambert = """
        const float RECIPROCAL_PI = 0.3183098861837907;

        vec3 lambertIrradiance(vec3 normal) {
            // AmbientLight: getAmbientLightIrradiance() is a straight pass-through.
            vec3 irradiance = uAmbientColor * uAmbientIntensity;
            // HemisphereLight sits at Object3D.DEFAULT_UP = (0,1,0) in core.js.
            float hemiWeight = 0.5 * dot(normal, vec3(0.0, 1.0, 0.0)) + 0.5;
            irradiance += mix(uHemiGround, uHemiSky, hemiWeight) * uHemiIntensity;
            return irradiance;
        }

        vec3 lambertLit(vec3 diffuse, vec3 normal) {
            vec3 n = normalize(normal);
            float dotNL = max(dot(n, uSunDir), 0.0);          // saturate() in the BRDF
            vec3 direct = uSunColor * uSunIntensity * dotNL;
            vec3 indirect = lambertIrradiance(n);
            return (direct + indirect) * RECIPROCAL_PI * diffuse;
        }
        """;

    /// <summary>
    /// <c>fog_fragment</c> for a linear <c>THREE.Fog(near, far)</c>:
    /// <c>mix(colour, fogColor, smoothstep(near, far, vFogDepth))</c> where
    /// <c>vFogDepth = -mvPosition.z</c>.
    /// </summary>
    private const string FogApply = """
        vec3 applyFog(vec3 linearColour, float viewDepth) {
            vec3 encoded = linearToSRGB(linearColour);
            float fogFactor = smoothstep(uFogNear, uFogFar, viewDepth);
            return mix(encoded, uFogColor, fogFactor);
        }
        """;

    private const string LightUniforms = """
        uniform vec3 uSunColor;
        uniform float uSunIntensity;
        uniform vec3 uSunDir;
        uniform vec3 uAmbientColor;
        uniform float uAmbientIntensity;
        uniform vec3 uHemiSky;
        uniform vec3 uHemiGround;
        uniform float uHemiIntensity;
        uniform vec3 uFogColor;
        uniform float uFogNear;
        uniform float uFogFar;
        uniform float uFogEnabled;
        """;

    private const string VertexCommon = """
        uniform mat4 uView;
        uniform mat4 uProj;
        """;

    // ------------------------------------------------------------------ terrain
    // MeshLambertMaterial { map: atlas, vertexColors: true, alphaTest: .5, fog: true }
    // used for the Opaque pass; the Overlay and Plants passes are the same material with
    // transparent: true (blending on) and, for Plants, side: DoubleSide (cull off).

    public const string TerrainVs = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec2 aTexCoord;
        layout(location = 2) in vec3 aNormal;
        layout(location = 3) in vec3 aColor;

        uniform mat4 uView;
        uniform mat4 uProj;

        out vec2 vTexCoord;
        out vec3 vNormal;
        out vec3 vColor;
        out float vFogDepth;

        void main() {
            vec4 world = vec4(aPosition, 1.0);
            vec4 view = uView * world;
            gl_Position = uProj * view;
            vFogDepth = -view.z;
            vTexCoord = aTexCoord;
            vNormal = aNormal;
            vColor = aColor;
        }
        """;

    public const string TerrainFs = """
        #version 330 core
        precision highp float;

        in vec2 vTexCoord;
        in vec3 vNormal;
        in vec3 vColor;
        in float vFogDepth;

        uniform sampler2D uMap;
        uniform float uAlphaTest;

        LIGHT_UNIFORMS
        out vec4 FragColor;

        COLOUR_SPACE
        LAMBERT
        FOG_APPLY

        void main() {
            vec4 texel = texture(uMap, vTexCoord);
            if (texel.a < uAlphaTest) discard;
            // The atlas is uploaded as plain RGBA8, so the sRGB decode that three.js gets
            // from an SRGB8_ALPHA8 internal format has to happen here.
            vec3 diffuse = sRGBToLinear(texel.rgb) * vColor;
            vec3 lit = lambertLit(diffuse, vNormal);
            vec3 encoded = mix(linearToSRGB(lit), applyFog(lit, vFogDepth), uFogEnabled);
            FragColor = vec4(encoded, texel.a);
        }
        """;

    // ------------------------------------------------------------------- entity
    // Covers MeshLambertMaterial (mobs, held block, block-shaped drops, particles) and
    // MeshBasicMaterial (flat item drops, uLit = 0). uUseMap = 0 gives the map-less
    // variants that core.js builds with only a colour.

    public const string EntityVs = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aTexCoord;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;

        out vec3 vNormal;
        out vec2 vTexCoord;
        out float vFogDepth;

        void main() {
            vec4 world = uModel * vec4(aPosition, 1.0);
            vec4 view = uView * world;
            gl_Position = uProj * view;
            vFogDepth = -view.z;
            // Every model matrix in the port is rotation + translation + uniform scale,
            // so mat3(uModel) is already a valid normal transform.
            vNormal = mat3(uModel) * aNormal;
            vTexCoord = aTexCoord;
        }
        """;

    public const string EntityFs = """
        #version 330 core
        precision highp float;

        in vec3 vNormal;
        in vec2 vTexCoord;
        in float vFogDepth;

        uniform sampler2D uMap;
        uniform float uUseMap;
        uniform float uLit;
        uniform vec3 uColor;
        uniform float uOpacity;
        uniform float uAlphaTest;
        uniform vec3 uEmissive;

        LIGHT_UNIFORMS
        out vec4 FragColor;

        COLOUR_SPACE
        LAMBERT
        FOG_APPLY

        void main() {
            vec4 diffuseColor = vec4(uColor, uOpacity);
            if (uUseMap > 0.5) {
                vec4 texel = texture(uMap, vTexCoord);
                if (texel.a < uAlphaTest) discard;
                diffuseColor *= vec4(sRGBToLinear(texel.rgb), texel.a);
            }
            vec3 outgoing = mix(diffuseColor.rgb, lambertLit(diffuseColor.rgb, vNormal), uLit) + uEmissive;
            vec3 encoded = mix(linearToSRGB(outgoing), applyFog(outgoing, vFogDepth), uFogEnabled);
            FragColor = vec4(encoded, diffuseColor.a);
        }
        """;

    // ------------------------------------------------------------------- sprite
    // SpriteMaterial { transparent, depthWrite:false, fog:false }. The sun/moon canvases
    // are CanvasTextures whose colorSpace was never set, so three.js samples them as
    // linear data — no EOTF here, matching the browser build pixel for pixel.

    public const string SpriteVs = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec2 aTexCoord;

        uniform mat4 uView;
        uniform mat4 uProj;

        out vec2 vTexCoord;
        void main() {
            gl_Position = uProj * uView * vec4(aPosition, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    public const string SpriteFs = """
        #version 330 core
        precision highp float;

        in vec2 vTexCoord;
        uniform sampler2D uMap;
        uniform vec4 uColor;
        out vec4 FragColor;

        COLOUR_SPACE

        void main() {
            vec4 texel = texture(uMap, vTexCoord);
            vec4 c = texel * uColor;
            FragColor = vec4(linearToSRGB(c.rgb), c.a);
        }
        """;

    // --------------------------------------------------------------------- line
    // LineBasicMaterial { color: 0x000000 }. LineBasicMaterial defaults to fog:true, so
    // the highlight fades into the sky at range exactly as it does in the browser.

    public const string LineVs = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;

        out float vFogDepth;
        void main() {
            vec4 view = uView * uModel * vec4(aPosition, 1.0);
            gl_Position = uProj * view;
            vFogDepth = -view.z;
        }
        """;

    public const string LineFs = """
        #version 330 core
        precision highp float;

        in float vFogDepth;
        uniform vec3 uColor;
        uniform vec3 uFogColor;
        uniform float uFogNear;
        uniform float uFogFar;
        uniform float uFogEnabled;
        out vec4 FragColor;

        COLOUR_SPACE
        FOG_APPLY

        void main() {
            // LineBasicMaterial colour goes through the same encode step.
            vec3 encoded = mix(linearToSRGB(uColor), applyFog(uColor, vFogDepth), uFogEnabled);
            FragColor = vec4(encoded, 1.0);
        }
        """;

    // ---------------------------------------------------------------------- hud
    // Stands in for the DOM overlay: an orthographic 2D pass in CSS pixels with the
    // y axis pointing down, so every number lifted out of style.css drops straight in.

    public const string HudVs = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aTexCoord;
        layout(location = 2) in vec4 aColor;

        uniform mat4 uProj;

        out vec2 vTexCoord;
        out vec4 vColor;
        void main() {
            gl_Position = uProj * vec4(aPosition, 0.0, 1.0);
            vTexCoord = aTexCoord;
            vColor = aColor;
        }
        """;

    public const string HudFs = """
        #version 330 core
        precision highp float;

        in vec2 vTexCoord;
        in vec4 vColor;
        uniform sampler2D uMap;
        uniform float uUseMap;
        out vec4 FragColor;

        void main() {
            vec4 c = vColor;
            if (uUseMap > 0.5) {
                vec4 texel = texture(uMap, vTexCoord);
                // premultiplied-ish: font glyphs store coverage in .a and white in .rgb
                c.rgb *= texel.rgb;
                c.a *= texel.a;
            }
            if (c.a <= 0.0039) discard;
            FragColor = c;
        }
        """;

    /// <summary>Splices the shared chunks into the sources above.</summary>
    public static string Expand(string source) => source
        .Replace("LIGHT_UNIFORMS", LightUniforms)
        .Replace("COLOUR_SPACE", ColourSpace)
        .Replace("LAMBERT", Lambert)
        .Replace("FOG_APPLY", FogApply)
        .Replace("VERTEX_COMMON", VertexCommon);
}
