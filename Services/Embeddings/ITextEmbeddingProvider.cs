using System.Threading;
using System.Threading.Tasks;

namespace MAS_BT.Services.Embeddings;

public interface ITextEmbeddingProvider
{
    Task<double[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default);
}
