using DiplomaMethod.Application.Utils.Onnx;
using DiplomaMethod.Core.Models.Documents;
using DiplomaMethod.Core.Options.TrOcr;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Text.Json;

namespace DiplomaMethod.Application.Extractors.Fallback.TrOcr
{
    public class TrOcrEngine : IDisposable
    {
        private static readonly string[] LogitsOnly = ["logits"];
        private static readonly bool[] FalseBool = [false];

        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoder;
        private readonly TrOcrPreprocessor _imagePreprocessor;
        private readonly TrOcrEngineOptions _config;
        private readonly Dictionary<int, string> _idToToken;
        private readonly bool _isMergedDecoder;
        private bool _disposed;

        public TrOcrEngine(TrOcrEngineOptions config, TrOcrPreprocessor imageProcessor)
        {
            _config = config;
            _imagePreprocessor = imageProcessor;
            _encoder = InferenceSessionInitializer.InitSession(config.EncoderModelPath);
            _decoder = InferenceSessionInitializer.InitSession(config.DecoderModelPath);
            _idToToken = LoadVocab(config.VocabPath);
            _isMergedDecoder = _decoder.InputMetadata.ContainsKey("use_cache_branch");
        }

        public string Recognize(LayoutImage image)
        {
            float[] pixelValues = _imagePreprocessor.PreprocessImage(image);
            float[] encoderOutput = RunEncoder(pixelValues);
            int[] tokenIds = GreedyDecode(encoderOutput);
            return DecodeTokens(tokenIds);
        }

        private float[] RunEncoder(float[] pixelValues)
        {
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("pixel_values",
                    new DenseTensor<float>(pixelValues, [1, 3, 384, 384]))
            };
            using var results = _encoder.Run(inputs);
            return [.. results[0].AsTensor<float>()];
        }

        private int[] GreedyDecode(float[] encoderHiddenStates)
        {
            var encoderTensor = new DenseTensor<float>(
                encoderHiddenStates, [1, _config.EncoderSeqLen, _config.EncoderHiddenSize]);

            var generatedIds = new List<int> { _config.DecoderStartTokenId };

            while (generatedIds.Count < _config.MaxLength)
            {
                float[] logits = _isMergedDecoder
                    ? RunDecoderMerged(generatedIds, encoderTensor)
                    : RunDecoderSimple(generatedIds, encoderTensor);

                int nextId = ArgmaxAtPosition(logits, generatedIds.Count - 1);
                if (nextId == _config.EosTokenId)
                    break;
                generatedIds.Add(nextId);
            }

            return [.. generatedIds.GetRange(1, generatedIds.Count - 1)];
        }

        // For standard (non-merged) decoder_model.onnx: only input_ids + encoder_hidden_states
        private float[] RunDecoderSimple(List<int> inputIds, DenseTensor<float> encoderHiddenStates)
        {
            var ids = inputIds.Select(i => (long)i).ToArray();

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids",
                    new DenseTensor<long>(ids, [1, inputIds.Count])),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenStates)
            };

            using var results = _decoder.Run(inputs, LogitsOnly);
            return [.. results[0].AsTensor<float>()];
        }

        // For merged decoder_model_merged.onnx: requires use_cache_branch + empty past KV placeholders
        private float[] RunDecoderMerged(List<int> inputIds, DenseTensor<float> encoderHiddenStates)
        {
            var ids = inputIds.Select(i => (long)i).ToArray();

            var inputs = new List<NamedOnnxValue>(3 + _config.NumLayers * 4)
            {
                NamedOnnxValue.CreateFromTensor("input_ids",
                    new DenseTensor<long>(ids, [1, inputIds.Count])),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenStates),
                NamedOnnxValue.CreateFromTensor("use_cache_branch",
                    new DenseTensor<bool>(FalseBool, [1]))
            };

            for (int i = 0; i < _config.NumLayers; i++)
            {
                inputs.Add(MakeEmptyKv($"past_key_values.{i}.decoder.key"));
                inputs.Add(MakeEmptyKv($"past_key_values.{i}.decoder.value"));
                inputs.Add(MakeEmptyKv($"past_key_values.{i}.encoder.key"));
                inputs.Add(MakeEmptyKv($"past_key_values.{i}.encoder.value"));
            }

            using var results = _decoder.Run(inputs, LogitsOnly);
            return [.. results[0].AsTensor<float>()];
        }

        private NamedOnnxValue MakeEmptyKv(string name) =>
            NamedOnnxValue.CreateFromTensor(name,
                new DenseTensor<float>([1, _config.NumHeads, 0, _config.HeadDim]));

        private int ArgmaxAtPosition(float[] logits, int position)
        {
            int offset = position * _config.VocabSize;
            int best = 0;
            float bestVal = float.MinValue;
            for (int i = 0; i < _config.VocabSize; i++)
            {
                if (logits[offset + i] > bestVal)
                {
                    bestVal = logits[offset + i];
                    best = i;
                }
            }
            return best;
        }

        private string DecodeTokens(int[] tokenIds)
        {
            var bpe = new System.Text.StringBuilder();
            foreach (int id in tokenIds)
            {
                if (_idToToken.TryGetValue(id, out string? token) && !IsSpecialToken(token))
                    bpe.Append(token);
            }
            // RoBERTa byte-level BPE: each char in the token string is a Unicode proxy for one raw byte.
            // Reverse the bytes_to_unicode mapping to get back the original bytes, then decode as UTF-8.
            var bytes = new List<byte>(bpe.Length);
            foreach (char c in bpe.ToString())
            {
                if (_unicodeToByte.TryGetValue(c, out byte b))
                    bytes.Add(b);
            }
            return System.Text.Encoding.UTF8.GetString([.. bytes]).Trim();
        }

        // Tokens enclosed in angle brackets (<s>, </s>, <pad>, <unk> etc.) are special control
        // tokens that must not appear in the decoded text output.
        private static bool IsSpecialToken(string token) =>
            token.Length >= 3 && token[0] == '<' && token[^1] == '>' && !token.Contains(' ');

        // Inverse of HuggingFace's bytes_to_unicode(): maps each BPE Unicode proxy char back to its byte.
        // Bytes 33-126, 161-172, 174-255 are self-mapped; the 68 non-printable bytes
        // (0-32, 127, 128-160, 173) are assigned sequentially to U+0100..U+0143.
        private static readonly Dictionary<char, byte> _unicodeToByte = BuildUnicodeToByte();

        private static Dictionary<char, byte> BuildUnicodeToByte()
        {
            var map = new Dictionary<char, byte>(256);
            for (int b = 33; b <= 126; b++) map[(char)b] = (byte)b;
            for (int b = 161; b <= 172; b++) map[(char)b] = (byte)b;
            for (int b = 174; b <= 255; b++) map[(char)b] = (byte)b;
            int n = 0;
            for (int b = 0; b < 256; b++)
            {
                if (!map.ContainsKey((char)b))
                    map[(char)(256 + n++)] = (byte)b;
            }
            return map;
        }

        private static Dictionary<int, string> LoadVocab(string path)
        {
            if (!File.Exists(path))
                return [];
            using var fs = File.OpenRead(path);
            var tokenToId = JsonSerializer.Deserialize<Dictionary<string, int>>(fs) ?? [];
            return tokenToId.ToDictionary(kv => kv.Value, kv => kv.Key);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                _encoder.Dispose();
                _decoder.Dispose();
            }
            _disposed = true;
        }
    }
}
