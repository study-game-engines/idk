﻿using OpenTK.Graphics.OpenGL4;
using IDKEngine.Render.Objects;

namespace IDKEngine.Render
{
    class VolumetricLighter
    {
        private int _samples;
        public int Samples
        {
            get => _samples;

            set
            {
                _samples = value;
                shaderProgram.Upload("Samples", _samples);
            }
        }

        private float _scattering;
        public float Scattering
        {
            get => _scattering;

            set
            {
                _scattering = value;
                shaderProgram.Upload("Scattering", _scattering);
            }
        }

        private int _renderedFrame;
        private int RenderedFrame
        {
            get => _renderedFrame;

            set
            {
                _renderedFrame = value;
                shaderProgram.Upload("RendererdFrame", _renderedFrame);
            }
        }


        public readonly Texture Result;
        private static readonly ShaderProgram shaderProgram =
            new ShaderProgram(new Shader(ShaderType.ComputeShader, System.IO.File.ReadAllText("res/shaders/VolumetricLight/compute.glsl")));
        public VolumetricLighter(int width, int height, int samples, float scattering)
        {
            Result = new Texture(TextureTarget2d.Texture2D);
            Result.SetFilter(TextureMinFilter.Linear, TextureMagFilter.Linear);
            Result.MutableAllocate(width, height, 1, PixelInternalFormat.Rgba16f);

            Samples = samples;
            Scattering = scattering;
        }

        public void Compute(Texture depth)
        {
            shaderProgram.Use();
            depth.BindToUnit(0);
            Result.BindToImageUnit(0, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
            
            GL.DispatchCompute((Result.Width + 8 - 1) / 8, (Result.Height + 4 - 1) / 4, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
            RenderedFrame = (RenderedFrame + 1) % 2;
        }

        public void SetSize(int width, int height)
        {
            Result.MutableAllocate(width, height, 1, Result.PixelInternalFormat);
        }
    }
}