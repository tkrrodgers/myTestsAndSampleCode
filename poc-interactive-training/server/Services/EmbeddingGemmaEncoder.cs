using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;
using OrtTensors = Microsoft.ML.OnnxRuntime.Tensors;

namespace PocInteractiveTraining.Server.Services;

// Real embeddinggemma-300m inference (int8 ONNX) with the Gemma SentencePiece tokenizer.
// Fails soft: if the model or tokenizer is missing or does not load, IsAvailable is false and callers
// fall back to structural-only features. Input/output names are read from the model at load time.
public sealed class EmbeddingGemmaEncoder : IDisposable
{
    private const int MaxTokens = 512;
    private readonly InferenceSession? _session;
    private readonly Tokenizer? _tokenizer;
    private readonly string[] _inputNames = [];
    private readonly object _gate = new();

    public bool IsAvailable { get; }
    public int Dimension { get; private set; }
    public string StatusMessage { get; }

    public EmbeddingGemmaEncoder(string modelDirectoryHint)
    {
        var modelDirectory = ResolveDirectory(modelDirectoryHint);
        var onnxPath = Path.Combine(modelDirectory, "onnx", "model_quantized.onnx");
        var tokenizerPath = Path.Combine(modelDirectory, "tokenizer.model");
        try
        {
            if (!File.Exists(onnxPath) || !File.Exists(tokenizerPath))
            {
                StatusMessage = $"embeddinggemma model not found near {modelDirectoryHint}; using structural features only.";
                return;
            }

            _session = new InferenceSession(onnxPath, new Microsoft.ML.OnnxRuntime.SessionOptions());
            using var tokenizerStream = File.OpenRead(tokenizerPath);
            _tokenizer = LlamaTokenizer.Create(tokenizerStream, true, false);
            _inputNames = _session.InputMetadata.Keys.ToArray();
            IsAvailable = true;
            StatusMessage = $"embeddinggemma-300m int8 ONNX loaded (inputs: {string.Join(", ", _inputNames)}).";
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            StatusMessage = $"embeddinggemma failed to load ({ex.Message}); using structural features only.";
        }
    }

    private static string ResolveDirectory(string hint)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(hint))
        {
            candidates.Add(hint);
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
            {
                candidates.Add(Path.Combine(directory.FullName, "poc-interactive-training", "models", "embeddinggemma-300m-onnx"));
                candidates.Add(Path.Combine(directory.FullName, "models", "embeddinggemma-300m-onnx"));
            }
        }

        return candidates.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "onnx", "model_quantized.onnx"))) ?? hint;
    }

    // Real Gemma token count when the tokenizer loaded; otherwise a conservative character estimate.
    public (int Tokens, bool Exact) CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0, _tokenizer is not null);
        }

        if (_tokenizer is null)
        {
            return ((int)Math.Ceiling(text.Length / 4.0), false);
        }

        try
        {
            return (_tokenizer.CountTokens(text), true);
        }
        catch (Exception)
        {
            return ((int)Math.Ceiling(text.Length / 4.0), false);
        }
    }

    public float[]? Encode(string text)
    {
        if (!IsAvailable || _session is null || _tokenizer is null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var ids = _tokenizer.EncodeToIds(text).ToArray();
            if (ids.Length == 0)
            {
                return null;
            }

            if (ids.Length > MaxTokens)
            {
                ids = ids[..MaxTokens];
            }

            var length = ids.Length;
            var inputIds = new OrtTensors.DenseTensor<long>([1, length]);
            var attentionMask = new OrtTensors.DenseTensor<long>([1, length]);
            var positionIds = new OrtTensors.DenseTensor<long>([1, length]);
            var tokenTypeIds = new OrtTensors.DenseTensor<long>([1, length]);
            for (var index = 0; index < length; index++)
            {
                inputIds[0, index] = ids[index];
                attentionMask[0, index] = 1;
                positionIds[0, index] = index;
                tokenTypeIds[0, index] = 0;
            }

            var feeds = new List<NamedOnnxValue>();
            foreach (var name in _inputNames)
            {
                var tensor = name switch
                {
                    "input_ids" => inputIds,
                    "attention_mask" => attentionMask,
                    "position_ids" => positionIds,
                    "token_type_ids" => tokenTypeIds,
                    _ => null
                };
                if (tensor is not null)
                {
                    feeds.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
                }
            }

            lock (_gate)
            {
                using var results = _session.Run(feeds);
                var outputs = results.ToArray();

                // Prefer an already-pooled sentence embedding output.
                var pooledOutput = outputs.FirstOrDefault(output =>
                    output.Name.Contains("sentence", StringComparison.OrdinalIgnoreCase) ||
                    output.Name.Contains("pooler", StringComparison.OrdinalIgnoreCase));
                if (pooledOutput is not null)
                {
                    var pooledTensor = pooledOutput.AsTensor<float>();
                    if (pooledTensor.Dimensions.Length == 2)
                    {
                        return Finalize(pooledTensor.ToArray());
                    }
                }

                // Otherwise mean-pool token embeddings [1, seq, hidden].
                var tokenOutput = outputs.FirstOrDefault(output => output.AsTensor<float>().Dimensions.Length == 3) ?? outputs[0];
                var tokenTensor = tokenOutput.AsTensor<float>();
                var dims = tokenTensor.Dimensions;
                if (dims.Length == 2)
                {
                    return Finalize(tokenTensor.ToArray());
                }

                var hidden = dims[2];
                var pooled = new float[hidden];
                for (var token = 0; token < length; token++)
                {
                    for (var dimension = 0; dimension < hidden; dimension++)
                    {
                        pooled[dimension] += tokenTensor[0, token, dimension];
                    }
                }

                for (var dimension = 0; dimension < hidden; dimension++)
                {
                    pooled[dimension] /= length;
                }

                return Finalize(pooled);
            }
        }
        catch
        {
            return null;
        }
    }

    public static float CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length != right.Length || left.Length == 0)
        {
            return 0f;
        }

        var dot = 0f;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
        }

        return dot; // vectors are L2-normalized in Finalize.
    }

    private float[] Finalize(float[] vector)
    {
        Dimension = vector.Length;
        var norm = 0.0;
        foreach (var value in vector)
        {
            norm += value * value;
        }

        var magnitude = (float)Math.Sqrt(norm);
        if (magnitude > 0)
        {
            for (var index = 0; index < vector.Length; index++)
            {
                vector[index] /= magnitude;
            }
        }

        return vector;
    }

    public void Dispose() => _session?.Dispose();
}
