﻿using System;
using System.IO;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;
using IDKEngine.Render.Objects;

namespace IDKEngine.Render
{
    class RasterPipeline : IDisposable
    {
        private bool _isVXGI;
        public bool IsVXGI
        {
            get => _isVXGI;

            set
            {
                _isVXGI = value;
                lightingProgram.Upload("IsVXGI", _isVXGI);
            }
        }

        public bool IsWireframe;
        public bool IsSSAO;
        public bool IsSSR;
        public bool IsVolumetricLighting;
        public bool IsVariableRateShading;
        public bool IsDebugRenderVXGIGrid;

        public readonly SSAO SSAO;
        public readonly SSR SSR;
        public readonly VolumetricLighter VolumetricLight;
        public readonly ShadingRateClassifier lightingVRS;
        public readonly Voxelizer Voxelizer;
        public readonly ConeTracer ConeTracer;

        public Texture Result;

        private Texture albedoAlphaTexture;
        private Texture normalSpecularTexture;
        private Texture emissiveRoughnessTexture;
        private Texture velocityTexture;
        private Texture depthTexture;

        private readonly ShaderProgram gBufferProgram;
        private readonly ShaderProgram lightingProgram;
        private readonly ShaderProgram skyBoxProgram;
        private readonly ShaderProgram mergeLightingProgram;

        private readonly BufferObject gBufferData;
        private readonly Framebuffer gBufferFBO;
        private readonly Framebuffer deferredLightingFBO;

        private GLSLGBufferData glslGBufferData;
        public unsafe RasterPipeline(int width, int height)
        {
            NvShadingRateImage[] shadingRates = new NvShadingRateImage[]
            {
                NvShadingRateImage.ShadingRate1InvocationPerPixelNv,
                NvShadingRateImage.ShadingRate1InvocationPer2X1PixelsNv,
                NvShadingRateImage.ShadingRate1InvocationPer2X2PixelsNv,
                NvShadingRateImage.ShadingRate1InvocationPer4X2PixelsNv,
                NvShadingRateImage.ShadingRate1InvocationPer4X4PixelsNv
            };
            lightingVRS = new ShadingRateClassifier(shadingRates, 
                new Shader(ShaderType.ComputeShader, File.ReadAllText("res/shaders/ShadingRateClassification/compute.glsl")),
                new Shader(ShaderType.ComputeShader, File.ReadAllText("res/shaders/ShadingRateClassification/debugCompute.glsl")), width, height);

            SSAO = new SSAO(width, height, 10, 0.1f, 2.0f);
            SSR = new SSR(width, height, 30, 8, 50.0f);
            VolumetricLight = new VolumetricLighter(width, height, 7, 0.758f, 50.0f, 5.0f, new Vector3(0.025f));
            Voxelizer = new Voxelizer(256, 256, 256, new Vector3(-28.0f, -3.0f, -17.0f), new Vector3(28.0f, 20.0f, 17.0f));
            ConeTracer = new ConeTracer(width, height);

            gBufferProgram = new ShaderProgram(
                    new Shader(ShaderType.VertexShader, File.ReadAllText("res/shaders/DeferredRendering/GBuffer/vertex.glsl")),
                    new Shader(ShaderType.FragmentShader, File.ReadAllText("res/shaders/DeferredRendering/GBuffer/fragment.glsl")));

            lightingProgram = new ShaderProgram(
                new Shader(ShaderType.VertexShader, File.ReadAllText("res/shaders/vertex.glsl")),
                new Shader(ShaderType.FragmentShader, File.ReadAllText("res/shaders/DeferredRendering/Lighting/fragment.glsl")));

            skyBoxProgram = new ShaderProgram(
                new Shader(ShaderType.VertexShader, File.ReadAllText("res/shaders/SkyBox/vertex.glsl")),
                new Shader(ShaderType.FragmentShader, File.ReadAllText("res/shaders/SkyBox/fragment.glsl")));

            mergeLightingProgram = new ShaderProgram(new Shader(ShaderType.ComputeShader, File.ReadAllText("res/shaders/MergeLighting/compute.glsl")));

            gBufferData = new BufferObject();
            gBufferData.ImmutableAllocate(sizeof(GLSLGBufferData), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            gBufferData.BindBufferBase(BufferRangeTarget.UniformBuffer, 6);

            gBufferFBO = new Framebuffer();
            deferredLightingFBO = new Framebuffer();

            IsWireframe = false;
            IsSSAO = true;
            IsSSR = false;
            IsVolumetricLighting = true;
            IsVariableRateShading = false;
            IsVXGI = false;

            SetSize(width, height);
        }

        public void Render(ModelSystem modelSystem, in Matrix4 cullProjViewMatrix, LightManager lightManager = null)
        {
            if (IsVXGI)
            {
                // when voxelizing make sure every mesh is rendered
                int i = 0;
                modelSystem.UpdateDrawCommandBuffer(0, modelSystem.DrawCommands.Length, (ref GLSLDrawCommand cmd) =>
                {
                    cmd.InstanceCount = modelSystem.Meshes[i++].InstanceCount;
                });

                Voxelizer.Render(modelSystem);
                Voxelizer.ResultVoxelsAlbedo.BindToUnit(1);
            }

            if (IsDebugRenderVXGIGrid)
            {
                Voxelizer.DebugRender(Result);
            }
            else
            {
                GL.Viewport(0, 0, Result.Width, Result.Height);

                if (IsWireframe)
                {
                    GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                }
                
                gBufferFBO.Bind();
                gBufferFBO.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                modelSystem.FrustumCull(cullProjViewMatrix);

                gBufferProgram.Use();
                modelSystem.Draw();
                GL.Flush();
                
                if (IsWireframe)
                {
                    GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                }

                if (IsSSAO)
                {
                    SSAO.Compute();
                }

                if (IsVXGI)
                {
                    ConeTracer.Compute(Voxelizer.ResultVoxelsAlbedo);
                }
                GL.Viewport(0, 0, Result.Width, Result.Height);

                if (IsVariableRateShading) ShadingRateClassifier.IsEnabled = true;
                deferredLightingFBO.Bind();
                lightingProgram.Use();
                GL.DepthMask(false);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                GL.DepthMask(true);
                ShadingRateClassifier.IsEnabled = false;

                if (IsVariableRateShading || lightingVRS.DebugValue != ShadingRateClassifier.DebugMode.NoDebug)
                {
                    lightingVRS.Compute(Result);
                }

                gBufferFBO.Bind();

                if (lightManager != null)
                {
                    lightManager.Draw();
                }

                GL.Disable(EnableCap.CullFace);
                GL.DepthFunc(DepthFunction.Lequal);

                skyBoxProgram.Use();
                GL.DrawArrays(PrimitiveType.Quads, 0, 24);

                GL.Enable(EnableCap.CullFace);
                GL.DepthFunc(DepthFunction.Less);

                if (IsVolumetricLighting)
                {
                    VolumetricLight.Compute();
                }

                if (IsSSR)
                {
                    SSR.Compute(Result);
                }

                Result.BindToImageUnit(0, 0, false, 0, TextureAccess.WriteOnly, Result.SizedInternalFormat);
                Result.BindToUnit(0);
                if (IsVXGI) ConeTracer.Result.BindToUnit(1);
                else Texture.UnbindFromUnit(1);            

                if (IsSSAO) SSAO.Result.BindToUnit(2);
                else Texture.UnbindFromUnit(2);

                if (IsSSR) SSR.Result.BindToUnit(3);
                else Texture.UnbindFromUnit(3);

                if (IsVolumetricLighting) VolumetricLight.Result.BindToUnit(4);
                else Texture.UnbindFromUnit(4);

                mergeLightingProgram.Use();
                GL.DispatchCompute((Result.Width + 8 - 1) / 8, (Result.Height + 8 - 1) / 8, 1);
                GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
            }

        }

        private void DisposeBindlessTextures()
        {
            if (albedoAlphaTexture != null) { Texture.UnmakeTextureHandleARB(glslGBufferData.AlbedoAlpha); albedoAlphaTexture.Dispose(); }
            if (normalSpecularTexture != null) { Texture.UnmakeTextureHandleARB(glslGBufferData.NormalSpecular); normalSpecularTexture.Dispose(); }
            if (emissiveRoughnessTexture != null) { Texture.UnmakeTextureHandleARB(glslGBufferData.EmissiveRoughness); emissiveRoughnessTexture.Dispose(); }
            if (velocityTexture != null) { Texture.UnmakeTextureHandleARB(glslGBufferData.Velocity); velocityTexture.Dispose(); }
            if (depthTexture != null) { Texture.UnmakeTextureHandleARB(glslGBufferData.Depth); depthTexture.Dispose(); }
        }

        public unsafe void SetSize(int width, int height)
        {
            SSAO.SetSize(width, height);
            SSR.SetSize(width, height);
            VolumetricLight.SetSize(width, height);
            lightingVRS.SetSize(width, height);
            ConeTracer.SetSize(width, height);

            if (Result != null) Result.Dispose();
            Result = new Texture(TextureTarget2d.Texture2D);
            Result.SetFilter(TextureMinFilter.Linear, TextureMagFilter.Linear);
            Result.SetWrapMode(TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
            Result.ImmutableAllocate(width, height, 1, SizedInternalFormat.Rgba16f);

            DisposeBindlessTextures();

            albedoAlphaTexture = new Texture(TextureTarget2d.Texture2D);
            albedoAlphaTexture.SetFilter(TextureMinFilter.Linear, TextureMagFilter.Linear);
            albedoAlphaTexture.SetWrapMode(TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
            albedoAlphaTexture.ImmutableAllocate(width, height, 1, SizedInternalFormat.Rgba8);
            glslGBufferData.AlbedoAlpha = albedoAlphaTexture.MakeTextureHandleARB();

            normalSpecularTexture = new Texture(TextureTarget2d.Texture2D);
            normalSpecularTexture.SetFilter(TextureMinFilter.Linear, TextureMagFilter.Linear);
            normalSpecularTexture.SetWrapMode(TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
            normalSpecularTexture.ImmutableAllocate(width, height, 1, SizedInternalFormat.Rgba8Snorm);
            glslGBufferData.NormalSpecular = normalSpecularTexture.MakeTextureHandleARB();

            emissiveRoughnessTexture = new Texture(TextureTarget2d.Texture2D);
            emissiveRoughnessTexture.SetFilter(TextureMinFilter.Linear, TextureMagFilter.Linear);
            emissiveRoughnessTexture.SetWrapMode(TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
            emissiveRoughnessTexture.ImmutableAllocate(width, height, 1, SizedInternalFormat.Rgba16f);
            glslGBufferData.EmissiveRoughness = emissiveRoughnessTexture.MakeTextureHandleARB();

            velocityTexture = new Texture(TextureTarget2d.Texture2D);
            velocityTexture.SetFilter(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
            velocityTexture.SetWrapMode(TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
            velocityTexture.ImmutableAllocate(width, height, 1, SizedInternalFormat.Rg16f);
            glslGBufferData.Velocity = velocityTexture.MakeTextureHandleARB();

            depthTexture = new Texture(TextureTarget2d.Texture2D);
            depthTexture.SetFilter(TextureMinFilter.Linear, TextureMagFilter.Linear);
            depthTexture.SetWrapMode(TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
            depthTexture.ImmutableAllocate(width, height, 1, (SizedInternalFormat)PixelInternalFormat.DepthComponent24);
            glslGBufferData.Depth = depthTexture.MakeTextureHandleARB();

            gBufferData.SubData(0, sizeof(GLSLGBufferData), glslGBufferData);

            gBufferFBO.SetRenderTarget(FramebufferAttachment.ColorAttachment0, Result);
            gBufferFBO.SetRenderTarget(FramebufferAttachment.ColorAttachment1, albedoAlphaTexture);
            gBufferFBO.SetRenderTarget(FramebufferAttachment.ColorAttachment2, normalSpecularTexture);
            gBufferFBO.SetRenderTarget(FramebufferAttachment.ColorAttachment3, emissiveRoughnessTexture);
            gBufferFBO.SetRenderTarget(FramebufferAttachment.ColorAttachment4, velocityTexture);
            gBufferFBO.SetRenderTarget(FramebufferAttachment.DepthAttachment, depthTexture);
            gBufferFBO.SetDrawBuffers(stackalloc DrawBuffersEnum[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3, DrawBuffersEnum.ColorAttachment4 });

            deferredLightingFBO.SetRenderTarget(FramebufferAttachment.ColorAttachment0, Result);
        }

        public void Dispose()
        {
            SSAO.Dispose();
            SSR.Dispose();
            VolumetricLight.Dispose();
            lightingVRS.Dispose();
            Voxelizer.Dispose();
            ConeTracer.Dispose();

            Result.Dispose();
            DisposeBindlessTextures();

            gBufferProgram.Dispose();
            lightingProgram.Dispose();
            skyBoxProgram.Dispose();
            mergeLightingProgram.Dispose();

            gBufferData.Dispose();

            gBufferFBO.Dispose();
            deferredLightingFBO.Dispose();
        }
    }
}