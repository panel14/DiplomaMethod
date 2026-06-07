using Microsoft.ML.OnnxRuntime;

namespace DiplomaMethod.Application.Utils.Onnx
{
    public static class InferenceSessionInitializer
    {
        public static InferenceSession InitSession(string modelPath)
        {
            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            if (IsCudaAvailable())
                sessionOptions.AppendExecutionProvider_CUDA();
            sessionOptions.AppendExecutionProvider_CPU();
            return new InferenceSession(modelPath, sessionOptions);
        }

        private static bool IsCudaAvailable() 
            => OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider");

    }
}
