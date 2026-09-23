using System.Threading;
using System.Threading.Tasks;

namespace Argus.Simulation.Core
{
    public interface IImageRenderer
    {
        string RendererName { get; }

        Task<ImageFrame> RenderAsync(
            RenderRequest request,
            CancellationToken cancellationToken);
    }
}
