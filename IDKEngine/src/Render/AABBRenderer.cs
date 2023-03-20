﻿using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using IDKEngine.Render.Objects;

namespace IDKEngine.Render
{
    class AABBRender : IDisposable
    {
        private readonly ShaderProgram shaderProgram;
        public AABBRender()
        {
            shaderProgram = new ShaderProgram(
                new Shader(ShaderType.VertexShader, File.ReadAllText("res/shaders/AABBRender/vertex.glsl")),
                new Shader(ShaderType.FragmentShader, File.ReadAllText("res/shaders/AABBRender/fragment.glsl")));
        }

        public void Render(Texture result, AABB aabb)
        {
            Framebuffer.Bind(0);

            GL.Viewport(0, 0, result.Width, result.Height);
            result.BindToImageUnit(0, 0, false, 0, TextureAccess.WriteOnly, result.SizedInternalFormat);

            GL.ColorMask(false, false, false, false);
            GL.DepthMask(false);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);

            shaderProgram.Upload(0, aabb.Min);
            shaderProgram.Upload(1, aabb.Max);

            shaderProgram.Use();
            GL.DrawArrays(PrimitiveType.Quads, 0, 24);

            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthMask(true);
            GL.ColorMask(true, true, true, true);
        }

        public void Dispose()
        {
            shaderProgram.Dispose();
        }
    }
}
