﻿using System;
using System.IO;
using System.Diagnostics;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.GraphicsLibraryFramework;
using IDKEngine.Render;
using IDKEngine.Render.Objects;
using IDKEngine.Shapes;

namespace IDKEngine
{
    public enum RenderMode : int
    {
        Rasterizer,
        PathTracer
    }

    public enum TemporalAntiAliasingMode : int
    {
        None,
        TAA,
        FSR2,
    }

    class Application : GameWindowBase
    {
        public Application(int width, int height, string title)
            : base(width, height, title, 4, 6)
        {
        }

        public const float EPSILON = 0.001f;
        public const float NEAR_PLANE = 0.1f, FAR_PLANE = 500.0f;
        public float CameraFovY = MathHelper.DegreesToRadians(102.0f);

        private float _resolutionScale = 1.0f;
        public float ResolutionScale
        {
            get => _resolutionScale;

            set
            {
                _resolutionScale = value;
                RenderPresentationResolution = RenderPresentationResolution;
            }
        }

        private Vector2i _renderPresentationResolution;
        public Vector2i RenderPresentationResolution
        {
            get => _renderPresentationResolution;

            set
            {
                _renderPresentationResolution = value;

                if (RenderMode == RenderMode.Rasterizer)
                {
                    if (RasterizerPipeline != null) RasterizerPipeline.SetSize(RenderResolution.X, RenderResolution.Y);
                    if (TaaResolve != null) TaaResolve.SetSize(RenderPresentationResolution.X, RenderPresentationResolution.Y);
                    if (FSR2Wrapper != null) FSR2Wrapper.SetSize(RenderPresentationResolution.X, RenderPresentationResolution.Y, RenderResolution.X, RenderResolution.Y);
                }

                if (RenderMode == RenderMode.PathTracer)
                {
                    if (PathTracer != null) PathTracer.SetSize(RenderResolution.X, RenderResolution.Y);
                }

                if (RenderMode == RenderMode.Rasterizer || RenderMode == RenderMode.PathTracer)
                {
                    if (Bloom != null) Bloom.SetSize(RenderPresentationResolution.X, RenderPresentationResolution.Y);
                    if (TonemapAndGamma != null) TonemapAndGamma.SetSize(RenderPresentationResolution.X, RenderPresentationResolution.Y);
                }
            }
        }

        private RenderMode _renderMode;
        public RenderMode RenderMode
        {
            get => _renderMode;

            set
            {
                if (RasterizerPipeline != null) { RasterizerPipeline.Dispose(); RasterizerPipeline = null; }
                if (PathTracer != null) { PathTracer.Dispose(); PathTracer = null; }

                if (value == RenderMode.Rasterizer)
                {
                    RasterizerPipeline = new RasterPipeline(RenderResolution.X, RenderResolution.Y);

                    if (TaaResolve != null) TaaResolve.SetSize(RenderPresentationResolution.X, RenderPresentationResolution.Y);
                    if (FSR2Wrapper != null) FSR2Wrapper.SetSize(RenderPresentationResolution.X, RenderPresentationResolution.Y, RenderResolution.X, RenderResolution.Y);
                }

                if (value == RenderMode.PathTracer)
                {
                    PathTracer = new PathTracer(ModelSystem.BVH, RenderResolution.X, RenderResolution.Y);
                }

                _renderMode = value;
            }
        }

        private TemporalAntiAliasingMode _temporalAntiAliasingMode;
        public TemporalAntiAliasingMode TemporalAntiAliasingMode
        {
            get => _temporalAntiAliasingMode;

            set
            {
                if (!FSR2Wrapper.IS_FSR2_SUPPORTED && value == TemporalAntiAliasingMode.FSR2)
                {
                    Logger.Log(Logger.LogLevel.Error, $"{TemporalAntiAliasingMode.FSR2} is Windows only");
                    return;
                }

                _temporalAntiAliasingMode = value;

                // TODO: Move TaaResolve and FSR2Wrapper into RasterizerPipeline
                // also store settings outsied of class so that they arent lost after disposal

                if (TemporalAntiAliasingMode == TemporalAntiAliasingMode.None)
                {
                    return;
                }

                if (TemporalAntiAliasingMode == TemporalAntiAliasingMode.TAA && TaaResolve == null)
                {
                    TaaResolve = new TAAResolve(RenderPresentationResolution.X, RenderPresentationResolution.Y);
                }

                if (TemporalAntiAliasingMode == TemporalAntiAliasingMode.FSR2 && FSR2Wrapper == null)
                {
                    FSR2Wrapper = new FSR2Wrapper(RenderPresentationResolution.X, RenderPresentationResolution.Y, RenderResolution.X, RenderResolution.Y);
                }
            }
        }

        public Vector2i RenderResolution => new Vector2i((int)(RenderPresentationResolution.X * ResolutionScale), (int)(RenderPresentationResolution.Y * ResolutionScale));
        public bool RenderGui { get; private set; }
        public int FPS { get; private set; }

        public bool IsBloom = true;
        public bool IsShadows = true;
        public int TAASamples = 6;

        public struct CameraCollisionDetection
        {
            public bool IsEnabled;
            public int TestSteps;
            public int ResponseSteps;
            public float EpsilonNormalOffset;
        }

        public CameraCollisionDetection CamCollisionSettings = new CameraCollisionDetection()
        {
            IsEnabled = false,
            TestSteps = 3,
            ResponseSteps = 12,
            EpsilonNormalOffset = 0.001f
        };

        public bool HasGravity = false;
        public float GravityDownForce = 70.0f;

        private int fpsCounter;
        private readonly Stopwatch fpsTimer = Stopwatch.StartNew();
        protected override unsafe void OnRender(float dT)
        {
            Update(dT);
            if (RenderMode == RenderMode.Rasterizer)
            {
                if (IsShadows)
                {
                    LightManager.RenderShadowMaps(ModelSystem);
                }

                if (RasterizerPipeline.IsConfigureGrid)
                {
                    RasterizerPipeline.Render(ModelSystem, GpuBasicData.ProjView);
                    TonemapAndGamma.Combine(RasterizerPipeline.Result);
                    MeshOutlineRenderer.Render(TonemapAndGamma.Result, new Box(RasterizerPipeline.Voxelizer.GridMin, RasterizerPipeline.Voxelizer.GridMax));
                }
                else
                {
                    RasterizerPipeline.Render(ModelSystem, GpuBasicData.ProjView, LightManager);

                    if (TemporalAntiAliasingMode == TemporalAntiAliasingMode.None)
                    {
                        if (IsBloom)
                        {
                            Bloom.Compute(RasterizerPipeline.Result);
                        }
                        TonemapAndGamma.Combine(RasterizerPipeline.Result, IsBloom ? Bloom.Result : null);
                    }
                    else if (TemporalAntiAliasingMode == TemporalAntiAliasingMode.TAA)
                    {
                        TaaResolve.RunTAA(RasterizerPipeline.Result);
                        if (IsBloom)
                        {
                            Bloom.Compute(TaaResolve.Result);
                        }
                        TonemapAndGamma.Combine(TaaResolve.Result, IsBloom ? Bloom.Result : null);
                    }
                    else if (TemporalAntiAliasingMode == TemporalAntiAliasingMode.FSR2)
                    {
                        FSR2Wrapper.RunFSR2(gpuTaaData.Jitter, RasterizerPipeline.Result, RasterizerPipeline.DepthTexture, RasterizerPipeline.VelocityTexture, dT * 1000.0f, NEAR_PLANE, FAR_PLANE, CameraFovY);
                        if (IsBloom)
                        {
                            Bloom.Compute(FSR2Wrapper.Result);
                        }
                        TonemapAndGamma.Combine(FSR2Wrapper.Result, IsBloom ? Bloom.Result : null);

                        // TODO: This is a hack to fix global UBO bindings modified by FSR2
                        taaDataBuffer.BindBufferBase(BufferRangeTarget.UniformBuffer, 3);
                        SkyBoxManager.skyBoxTextureBuffer.BindBufferBase(BufferRangeTarget.UniformBuffer, 4);
                        RasterizerPipeline.Voxelizer.voxelizerDataBuffer.BindBufferBase(BufferRangeTarget.UniformBuffer, 5);
                        RasterizerPipeline.gBufferData.BindBufferBase(BufferRangeTarget.UniformBuffer, 6);
                    }
                    RasterizerPipeline.LightingVRS.DebugRender(TonemapAndGamma.Result);

                }
            }
            else if (RenderMode == RenderMode.PathTracer)
            {
                PathTracer.Compute();

                if (IsBloom)
                {
                    Bloom.Compute(PathTracer.Result);
                }

                TonemapAndGamma.Combine(PathTracer.Result, IsBloom ? Bloom.Result : null);
            }

            if (gui.SelectedEntity.Type != Gui.EntityType.None)
            {
                Box box = new Box();
                if (gui.SelectedEntity.Type == Gui.EntityType.Mesh)
                {
                    GpuBlasNode node = ModelSystem.BVH.Tlas.Blases[gui.SelectedEntity.Index].Root;
                    box.Min = node.Min;
                    box.Max = node.Max;

                    GpuDrawElementsCmd cmd = ModelSystem.DrawCommands[gui.SelectedEntity.Index];
                    box.Transform(ModelSystem.MeshInstances[cmd.BaseInstance].ModelMatrix);
                }
                else
                {
                    LightManager.TryGetLight(gui.SelectedEntity.Index, out Light abstractLight);
                    ref GpuLight light = ref abstractLight.GpuLight;

                    box.Min = new Vector3(light.Position) - new Vector3(light.Radius);
                    box.Max = new Vector3(light.Position) + new Vector3(light.Radius);
                }

                MeshOutlineRenderer.Render(TonemapAndGamma.Result, box);
            }

            Framebuffer.Bind(0);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.Viewport(0, 0, WindowFramebufferSize.X, WindowFramebufferSize.Y);
            if (RenderGui)
            {
                gui.Draw(this, (float)dT);
            }
            else
            {
                TonemapAndGamma.Result.BindToUnit(0);
                finalProgram.Use();
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);

            fpsCounter++;
        }

        private unsafe void Update(float dT)
        {
            if (fpsTimer.ElapsedMilliseconds >= 1000)
            {
                FPS = fpsCounter;
                WindowTitle = $"IDKEngine FPS: {FPS}";
                fpsCounter = 0;
                fpsTimer.Restart();
            }

            // Keyboard Inputs
            {
                if (KeyboardState[Keys.Escape] == InputState.Pressed)
                {
                    ShouldClose();
                }

                if (KeyboardState[Keys.V] == InputState.Touched)
                {
                    WindowVSync = !WindowVSync;
                }
                if (KeyboardState[Keys.G] == InputState.Touched)
                {
                    RenderGui = !RenderGui;
                    if (!RenderGui)
                    {
                        RenderPresentationResolution = new Vector2i(WindowFramebufferSize.X, WindowFramebufferSize.Y);
                    }
                }
                if (KeyboardState[Keys.F11] == InputState.Touched)
                {
                    WindowFullscreen = !WindowFullscreen;
                }
            }

            if (gui.FrameRecState != Gui.FrameRecorderState.Replaying)
            {
                if (KeyboardState[Keys.E] == InputState.Touched && !ImGuiNET.ImGui.GetIO().WantCaptureKeyboard)
                {
                    if (MouseState.CursorMode == CursorModeValue.CursorDisabled)
                    {
                        MouseState.CursorMode = CursorModeValue.CursorNormal;
                        Camera.Velocity = Vector3.Zero;
                    }
                    else
                    {
                        MouseState.CursorMode = CursorModeValue.CursorDisabled;
                    }
                }

                if (MouseState.CursorMode == CursorModeValue.CursorDisabled)
                {
                    Camera.ProcessInputs(KeyboardState, MouseState);
                    if (HasGravity)
                    {
                        Camera.ThisFrameAcceleration.Y += -GravityDownForce;
                    }

                    Camera.AdvanceSimulation(dT);

                }
            }
            gui.Update(this);

            //ModelSystem.BVH.TlasBuild();

            if (CamCollisionSettings.IsEnabled)
            {
                for (int i = 0; i < CamCollisionSettings.ResponseSteps; i++)
                {
                    Sphere boundingVolume = new Sphere(Camera.PrevPosition, 0.5f);
                    Plane hitPlane;
                    float penetrationDepth;
                    bool hit = CollisionRoutine(ModelSystem, CamCollisionSettings, Camera.Position, &boundingVolume, &hitPlane, &penetrationDepth);
                    if (hit)
                    {
                        Vector3 newVelocity = Plane.Project(Camera.Velocity, hitPlane);
                        Camera.Velocity = newVelocity;

                        boundingVolume.Center += hitPlane.Normal * (penetrationDepth + CamCollisionSettings.EpsilonNormalOffset);
                        Camera.Position = boundingVolume.Center;

                        Camera.AdvanceSimulation(dT);
                    }
                    else
                    {
                        break;
                    }
                }

                // We need to use raw pointers here instead of ref & out, because
                // "CS1628 - Cannot use in ref or out parameter inside an anonymous method, lambda expression, or query expression."
                static bool CollisionRoutine(ModelSystem modelSystem, in CameraCollisionDetection settings, Vector3 newPosition, Sphere* refBoundingVolume, Plane* outHitPlane, float* outPenetrationDepth)
                {
                    *outPenetrationDepth = float.MinValue;
                    *outHitPlane = new Plane();

                    Vector3 cameraStepSize = (newPosition - refBoundingVolume->Center) / settings.TestSteps;
                    for (int i = 1; i <= settings.TestSteps; i++)
                    {
                        refBoundingVolume->Center += cameraStepSize;
                        Box playerBox = new Box(refBoundingVolume->Center - new Vector3(refBoundingVolume->Radius), refBoundingVolume->Center + new Vector3(refBoundingVolume->Radius));

                        float cosTheta = 0.0f;
                        modelSystem.BVH.Intersect(playerBox, (in BVH.PrimitiveHitInfo hitInfo) =>
                        {
                            Matrix4 model = modelSystem.MeshInstances[hitInfo.InstanceID].ModelMatrix;
                            Triangle worldSpaceTri = Triangle.Transformed(GpuTypes.Conversions.ToTriangle(hitInfo.Triangle), model);

                            Vector3 closestPointOnTri = Intersections.TriangleClosestPoint(worldSpaceTri, refBoundingVolume->Center);
                            float distance = Vector3.Distance(closestPointOnTri, refBoundingVolume->Center);
                            float thisPenetrationDepth = refBoundingVolume->Radius - distance;
                            if (thisPenetrationDepth > 0.0f)
                            {
                                Plane thisHitPlane = new Plane(worldSpaceTri.Normal);

                                Vector3 hitPointToCameraDir = (refBoundingVolume->Center - closestPointOnTri) / distance;
                                float thisCosTheta = Vector3.Dot(thisHitPlane.Normal, hitPointToCameraDir);
                                if (thisCosTheta < 0.0f)
                                {
                                    thisHitPlane.Normal *= -1.0f;
                                }

                                if (MathF.Abs(thisCosTheta) > MathF.Abs(cosTheta))
                                {
                                    cosTheta = thisCosTheta;
                                    *outHitPlane = thisHitPlane;
                                    *outPenetrationDepth = thisPenetrationDepth;
                                }
                            }
                        });

                        if (*outPenetrationDepth != float.MinValue)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            // TAA
            {
                if (TemporalAntiAliasingMode == TemporalAntiAliasingMode.None || TemporalAntiAliasingMode == TemporalAntiAliasingMode.TAA)
                {
                    gpuTaaData.MipmapBias = 0.0f;
                }
                if (TemporalAntiAliasingMode == TemporalAntiAliasingMode.TAA)
                {
                    gpuTaaData.Samples = TAASamples;
                }
                if (TemporalAntiAliasingMode == TemporalAntiAliasingMode.FSR2)
                {
                    const float manualBias = 0.25f;
                    gpuTaaData.MipmapBias = FSR2Wrapper.GetRecommendedMipmapBias(RenderResolution.X, RenderPresentationResolution.X) + manualBias;
                    gpuTaaData.Samples = FSR2Wrapper.GetRecommendedSampleCount(RenderResolution.X, RenderPresentationResolution.X);
                }
                if (TemporalAntiAliasingMode != TemporalAntiAliasingMode.None)
                {
                    Vector2 jitter = MyMath.GetHalton2D((int)GpuBasicData.Frame % gpuTaaData.Samples, 2, 3);
                    gpuTaaData.Jitter = (jitter * 2.0f - new Vector2(1.0f)) / RenderResolution;
                }
                gpuTaaData.TemporalAntiAliasingMode = TemporalAntiAliasingMode;
                taaDataBuffer.SubData(0, sizeof(GpuTaaData), gpuTaaData);
            }

            LightManager.UpdateBufferData();

            // Updating global basicData Buffer
            {
                GpuBasicData.Projection = MyMath.CreatePerspectiveFieldOfViewDepthZeroToOne(CameraFovY, RenderPresentationResolution.X / (float)RenderPresentationResolution.Y, NEAR_PLANE, FAR_PLANE);
                GpuBasicData.InvProjection = GpuBasicData.Projection.Inverted();
                GpuBasicData.NearPlane = NEAR_PLANE;
                GpuBasicData.FarPlane = FAR_PLANE;
                GpuBasicData.DeltaUpdate = dT;
                GpuBasicData.PrevProjView = GpuBasicData.ProjView;
                GpuBasicData.PrevView = GpuBasicData.View;
                GpuBasicData.View = Camera.GenerateViewMatrix(Camera.Position, Camera.ViewDir, Camera.UpVector);
                GpuBasicData.InvView = GpuBasicData.View.Inverted();
                GpuBasicData.ProjView = GpuBasicData.View * GpuBasicData.Projection;
                GpuBasicData.InvProjView = GpuBasicData.ProjView.Inverted();
                GpuBasicData.CameraPos = Camera.Position;
                GpuBasicData.Time = WindowTime;
                GpuBasicData.Frame++;
                basicDataBuffer.SubData(0, sizeof(GpuBasicData), GpuBasicData);
            }

            bool anyMeshInstanceMoved = false;
            // Updating MeshInstance Buffer
            {
                ModelSystem.UpdateMeshInstanceBuffer(0, ModelSystem.MeshInstances.Length);
                for (int i = 0; i < ModelSystem.MeshInstances.Length; i++)
                {
                    if (ModelSystem.MeshInstances[i].DidMove())
                    {
                        ModelSystem.MeshInstances[i].SetPrevToCurrentMatrix();
                        anyMeshInstanceMoved = true;
                    }
                }
            }

            // Resetting Path Tracer if necessary
            {
                bool cameraMoved = GpuBasicData.PrevProjView != GpuBasicData.ProjView;
                if ((RenderMode == RenderMode.PathTracer) && (cameraMoved || anyMeshInstanceMoved))
                {
                    PathTracer.ResetRenderProcess();
                }
            }
        }

        private Gui gui;
        private ShaderProgram finalProgram;

        public Camera Camera;
        public ModelSystem ModelSystem;
        public FrameStateRecorder<FrameState> FrameRecorder;

        public Bloom Bloom;
        public TonemapAndGammaCorrecter TonemapAndGamma;
        public TAAResolve TaaResolve;
        public FSR2Wrapper FSR2Wrapper;
        public LightManager LightManager;
        public BoxRenderer MeshOutlineRenderer;

        public RasterPipeline RasterizerPipeline;
        public PathTracer PathTracer;

        private BufferObject basicDataBuffer;
        public GpuBasicData GpuBasicData;

        private BufferObject taaDataBuffer;
        private GpuTaaData gpuTaaData;
        protected override unsafe void OnStart()
        {
            Logger.Log(Logger.LogLevel.Info, $"API: {Helper.API}");
            Logger.Log(Logger.LogLevel.Info, $"GPU: {Helper.GPU}");

            if (Helper.APIVersion < 4.6)
            {
                Logger.Log(Logger.LogLevel.Fatal, "Your system does not support OpenGL 4.6. Press Enter to exit");
                Console.ReadLine();
                Environment.Exit(0);
            }
            if (!Helper.IsExtensionsAvailable("GL_ARB_bindless_texture"))
            {
                Logger.Log(Logger.LogLevel.Fatal, "Your system does not support GL_ARB_bindless_texture. Press Enter to exit");
                Console.ReadLine();
                Environment.Exit(0);
            }

            GL.Enable(EnableCap.DebugOutput);
            GL.Enable(EnableCap.DebugOutputSynchronous);
            GL.DebugMessageCallback(Helper.GLDebugCallbackFuncPtr, IntPtr.Zero);
            GL.PointSize(1.3f);
            Helper.SetDepthConvention(Helper.DepthConvention.ZeroToOne);
            GL.Disable(EnableCap.Multisample);
            GL.Enable(EnableCap.TextureCubeMapSeamless);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.ScissorTest);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);

            RenderPresentationResolution = WindowFramebufferSize;
            RenderMode = RenderMode.Rasterizer;

            basicDataBuffer = new BufferObject();
            basicDataBuffer.ImmutableAllocate(sizeof(GpuBasicData), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            basicDataBuffer.BindBufferBase(BufferRangeTarget.UniformBuffer, 0);

            gpuTaaData.Samples = 6;
            gpuTaaData.Jitter = new Vector2(0.0f);
            taaDataBuffer = new BufferObject();
            taaDataBuffer.ImmutableAllocate(sizeof(GpuTaaData), gpuTaaData, BufferStorageFlags.DynamicStorageBit);
            taaDataBuffer.BindBufferBase(BufferRangeTarget.UniformBuffer, 3);

            finalProgram = new ShaderProgram(
                new Shader(ShaderType.VertexShader, File.ReadAllText("res/shaders/vertex.glsl")),
                new Shader(ShaderType.FragmentShader, File.ReadAllText("res/shaders/fragment.glsl")));
            Camera = new Camera(new Vector3(7.63f, 2.71f, 0.8f), new Vector3(0.0f, 1.0f, 0.0f), -165.4f, 7.4f);
            //camera = new Camera(new Vector3(-8.0f, 2.00f, -0.5f), new Vector3(0.0f, 1.0f, 0.0f), -183.5f, 0.5f, 0.1f, 0.25f);

            SkyBoxManager.Init(new string[]
            {
                "res/textures/environmentMap/posx.jpg",
                "res/textures/environmentMap/negx.jpg",
                "res/textures/environmentMap/posy.jpg",
                "res/textures/environmentMap/negy.jpg",
                "res/textures/environmentMap/posz.jpg",
                "res/textures/environmentMap/negz.jpg"
            });

            LightManager = new LightManager(12, 12);
            MeshOutlineRenderer = new BoxRenderer();
            Bloom = new Bloom(RenderPresentationResolution.X, RenderPresentationResolution.Y, 1.0f, 3.0f);
            TonemapAndGamma = new TonemapAndGammaCorrecter(RenderPresentationResolution.X, RenderPresentationResolution.Y);
            TemporalAntiAliasingMode = TemporalAntiAliasingMode.TAA;
            ModelSystem = new ModelSystem();

            if (true)
            {
                Model sponza = new Model("res/models/Sponza/glTF/Sponza.gltf", Matrix4.CreateScale(1.815f) * Matrix4.CreateTranslation(0.0f, -1.0f, 0.0f));
                sponza.Meshes[63].EmissiveBias = 10.0f;
                sponza.Meshes[70].EmissiveBias = 20.0f;
                sponza.Meshes[3].EmissiveBias = 12.0f;
                sponza.Meshes[99].EmissiveBias = 15.0f;
                sponza.Meshes[97].EmissiveBias = 9.0f;
                sponza.Meshes[42].EmissiveBias = 20.0f;
                sponza.Meshes[38].EmissiveBias = 20.0f;
                sponza.Meshes[40].EmissiveBias = 20.0f;
                sponza.Meshes[42].EmissiveBias = 20.0f;
                sponza.Meshes[46].SpecularBias = 1.0f;
                sponza.Meshes[46].RoughnessBias = -0.436f;

                Model lucy = new Model("res/models/Lucy/Lucy.gltf", Matrix4.CreateScale(0.8f) * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(90.0f)) * Matrix4.CreateTranslation(-1.68f, 2.3f, 0.0f));
                lucy.Meshes[0].SpecularBias = -1.0f;
                lucy.Meshes[0].RefractionChance = 0.98f;
                lucy.Meshes[0].IOR = 1.174f;
                lucy.Meshes[0].Absorbance = new Vector3(0.81f, 0.18f, 0.0f);
                lucy.Meshes[0].RoughnessBias = -1.0f;

                Model helmet = new Model("res/models/Helmet/Helmet.gltf");
                helmet.MeshInstances[0].ModelMatrix *= Matrix4.CreateRotationY(MathF.PI / 4.0f);

                //Model test = new Model(@"C:\Users\Julian\Downloads\Models\CornellBox\scene.gltf", Matrix4.CreateTranslation(-100.0f, 50.0f, 0.0f));
                //Model ramp = new Model(@"C:\Users\Julian\Downloads\Models\Ramp\Ramp.gltf", Matrix4.CreateTranslation(-100.0f, 0.0f, 0.0f) * Matrix4.CreateTranslation(0.0f, 0.0f, 0.0f));

                ModelSystem.Add(sponza, lucy, helmet);

                LightManager.AddLight(new Light(new Vector3(-4.5f, 5.7f, -2.0f), new Vector3(3.5f, 0.8f, 0.9f) * 6.3f, 0.3f));
                LightManager.AddLight(new Light(new Vector3(-0.5f, 5.7f, -2.0f), new Vector3(0.5f, 3.8f, 0.9f) * 6.3f, 0.3f));
                LightManager.AddLight(new Light(new Vector3(4.5f, 5.7f, -2.0f), new Vector3(0.5f, 0.8f, 3.9f) * 6.3f, 0.3f));
                //LightManager.AddLight(new Light(new Vector3(-6.0f, 21.0f, 2.95f), new Vector3(1.0f) * 200.0f, 1.0f)); // alt Color: new Vector3(50.4f, 35.8f, 25.2f)
                //LightManager.CreatePointShadowForLight(new PointShadow(1536, 0.5f, 60.0f), LightManager.Count - 1);
                for (int i = 0; i < 3; i++)
                {
                    PointShadow pointShadow = new PointShadow(512, 0.5f, 60.0f);
                    LightManager.CreatePointShadowForLight(pointShadow, i);
                }
            }
            else
            {
                Model a = new Model(@"C:\Users\Julian\Downloads\Models\IntelSponza\Base\NewSponza_Main_glTF_002.gltf");
                a.MeshInstances[28].ModelMatrix = Matrix4.CreateTranslation(-1000.0f, 0.0f, 0.0f);
                a.MeshInstances[89].ModelMatrix = Matrix4.CreateTranslation(-1000.0f, 0.0f, 0.0f);
                a.MeshInstances[271].ModelMatrix = Matrix4.CreateTranslation(-1000.0f, 0.0f, 0.0f);
                a.Meshes[288].SpecularBias = 1.0f;
                a.Meshes[288].RoughnessBias = -0.64f;
                a.Meshes[288].NormalMapStrength = 0.0f;
                a.Meshes[272].SpecularBias = 1.0f;
                a.Meshes[272].RoughnessBias = -0.82f;
                a.Meshes[93].EmissiveBias = 20.0f;
                a.Meshes[96].EmissiveBias = 20.0f;
                a.Meshes[99].EmissiveBias = 20.0f;
                a.Meshes[102].EmissiveBias = 20.0f;
                a.Meshes[105].EmissiveBias = 20.0f;
                a.Meshes[108].EmissiveBias = 20.0f;
                a.Meshes[111].EmissiveBias = 20.0f;
                a.Meshes[246].EmissiveBias = 20.0f;
                a.Meshes[291].EmissiveBias = 20.0f;
                a.Meshes[294].EmissiveBias = 20.0f;
                a.Meshes[297].EmissiveBias = 20.0f;
                a.Meshes[300].EmissiveBias = 20.0f;
                a.Meshes[303].EmissiveBias = 20.0f;
                a.Meshes[306].EmissiveBias = 20.0f;
                a.Meshes[312].EmissiveBias = 20.0f;
                a.Meshes[315].EmissiveBias = 20.0f;
                a.Meshes[318].EmissiveBias = 20.0f;
                a.Meshes[321].EmissiveBias = 20.0f;
                a.Meshes[324].EmissiveBias = 20.0f;
                a.Meshes[376].EmissiveBias = 20.0f;
                a.Meshes[379].EmissiveBias = 20.0f;
                Model b = new Model(@"C:\Users\Julian\Downloads\Models\IntelSponza\Curtains\NewSponza_Curtains_glTF.gltf");
                Model c = new Model(@"C:\Users\Julian\Downloads\Models\IntelSponza\Ivy\NewSponza_IvyGrowth_glTF.gltf");
                Model d = new Model(@"C:\Users\Julian\Downloads\Models\IntelSponza\Tree\NewSponza_CypressTree_glTF.gltf");
                LightManager.AddLight(new Light(new Vector3(-6.256f, 8.415f, -0.315f), new Vector3(30.46f, 25.17f, 25.75f), 0.3f));
                LightManager.CreatePointShadowForLight(new PointShadow(512, 1.0f, 60.0f), 0);
                RasterizerPipeline.IsVXGI = false;
                RasterizerPipeline.Voxelizer.GridMin = new Vector3(-18.0f, -1.2f, -11.9f);
                RasterizerPipeline.Voxelizer.GridMax = new Vector3(21.3f, 19.7f, 17.8f);
                RasterizerPipeline.VolumetricLight.Strength = 10.0f;
                RasterizerPipeline.ConeTracer.MaxSamples = 4;
                ModelSystem.Add(a, b, c, d);
            }


            RenderGui = true;
            WindowVSync = true;
            MouseState.CursorMode = CursorModeValue.CursorNormal;
            FrameRecorder = new FrameStateRecorder<FrameState>();
            gui = new Gui(WindowFramebufferSize.X, WindowFramebufferSize.Y);

            GC.Collect();
        }

        protected override void OnEnd()
        {

        }

        protected override void OnResize()
        {
            gui.Backend.SetSize(WindowFramebufferSize.X, WindowFramebufferSize.Y);

            // if we don't render to the screen via gui always make viewport match window size
            if (!RenderGui)
            {
                RenderPresentationResolution = new Vector2i(WindowFramebufferSize.X, WindowFramebufferSize.Y);
            }
        }

        protected override void OnKeyPress(char key)
        {
            gui.Backend.PressChar(key);
        }
    }
}
