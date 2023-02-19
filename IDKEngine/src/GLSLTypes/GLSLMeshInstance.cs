﻿using OpenTK.Mathematics;

namespace IDKEngine
{
    struct GLSLMeshInstance
    {
        private Matrix4 _modelMatrix;
        public Matrix4 ModelMatrix
        {
            get => _modelMatrix;

            set
            {
                if (_modelMatrix == Matrix4.Zero)
                {
                    _modelMatrix = value;
                }

                PrevModelMatrix = _modelMatrix;
                _modelMatrix = value;
                InvModelMatrix = Matrix4.Invert(_modelMatrix);
            }
        }

        public Matrix4 InvModelMatrix { get; private set; }
        public Matrix4 PrevModelMatrix { get; private set; }

        public bool ResetPrevModelMatrixToCurrent()
        {
            bool changed = PrevModelMatrix != ModelMatrix;
            PrevModelMatrix = ModelMatrix;
            return changed;
        }
    }
}