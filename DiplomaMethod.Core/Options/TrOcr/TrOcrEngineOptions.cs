
namespace DiplomaMethod.Core.Options.TrOcr
{
    public record TrOcrEngineOptions
    {
        public string EncoderModelPath { get; init; } = "";
        public string DecoderModelPath { get; init; } = "";
        public string VocabPath { get; init; } = "";
        public int DecoderStartTokenId { get; init; } = 2;
        public int EosTokenId { get; init; } = 2;
        public int MaxLength { get; init; } = 20;
        public int NumLayers { get; init; } = 12;
        public int NumHeads { get; init; } = 16;
        public int HeadDim { get; init; } = 64;
        public int EncoderSeqLen { get; init; } = 577;
        public int EncoderHiddenSize { get; init; } = 768;
        public int VocabSize { get; init; } = 50265;
    }
}
