using DiplomaMethod.Core.Options.TrOcr;
using System.Text.Json;

namespace DiplomaMethod.Application.Utils.TrOcr
{
    /// <summary>
    /// Reads TrOCR model architecture and generation parameters from HuggingFace
    /// config.json and generation_config.json files.
    /// </summary>
    public static class TrOcrModelConfigReader
    {
        public static TrOcrEngineOptions Read(string configJsonPath)
        {
            using var configDoc = JsonDocument.Parse(File.ReadAllText(configJsonPath));
            var root = configDoc.RootElement;

            var enc = root.GetProperty("encoder");
            int hiddenSize = enc.GetProperty("hidden_size").GetInt32();
            int imageSize = enc.GetProperty("image_size").GetInt32();
            int patchSize = enc.GetProperty("patch_size").GetInt32();
            int patches = imageSize / patchSize;
            int seqLen = patches * patches + 1; // +1 for [CLS] token

            var dec = root.GetProperty("decoder");
            int numLayers = dec.GetProperty("decoder_layers").GetInt32();
            int numHeads = dec.GetProperty("decoder_attention_heads").GetInt32();
            int dModel = dec.GetProperty("d_model").GetInt32();
            int vocabSize = dec.GetProperty("vocab_size").GetInt32();

            // Generation params: prefer generation_config.json, fall back to config.json
            int maxLength = dec.GetProperty("max_length").GetInt32();
            int decoderStartTokenId = GetInt(root, "decoder_start_token_id")
                ?? GetInt(dec, "decoder_start_token_id")
                ?? 2;
            int eosTokenId = GetInt(root, "eos_token_id")
                ?? GetInt(dec, "eos_token_id")
                ?? 2;

            var genConfigPath = Path.Combine(
                Path.GetDirectoryName(configJsonPath)!, "generation_config.json");

            if (File.Exists(genConfigPath))
            {
                using var genDoc = JsonDocument.Parse(File.ReadAllText(genConfigPath));
                var gen = genDoc.RootElement;
                if (GetInt(gen, "max_length") is int ml) maxLength = ml;
                if (GetInt(gen, "decoder_start_token_id") is int dst) decoderStartTokenId = dst;
                if (GetInt(gen, "eos_token_id") is int eos) eosTokenId = eos;
            }

            return new TrOcrEngineOptions
            {
                NumLayers = numLayers,
                NumHeads = numHeads,
                HeadDim = dModel / numHeads,
                VocabSize = vocabSize,
                EncoderHiddenSize = hiddenSize,
                EncoderSeqLen = seqLen,
                MaxLength = maxLength,
                DecoderStartTokenId = decoderStartTokenId,
                EosTokenId = eosTokenId
            };
        }

        private static int? GetInt(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop)
                && prop.ValueKind == JsonValueKind.Number
                && prop.TryGetInt32(out int value))
                return value;
            return null;
        }
    }
}
