using System.Threading;
using System.Threading.Tasks;

namespace Argus.Simulation.Core
{
    // Renders the exposures a camera model requests (D5). Each ImageFrame it returns owns its Data:
    // a fresh array the renderer never writes again. The recorder queues frames by reference (#99),
    // so a reused buffer would put one camera's pixels in another camera's frame. That breaks the
    // dataset (D6), and could show a controller ground-truth pixels, such as the nadir camera's, in a
    // visible camera's observation (D7). Core does not check it.
    public interface IImageRenderer
    {
        string RendererName { get; }

        Task<ImageFrame> RenderAsync(
            RenderRequest request,
            CancellationToken cancellationToken);
    }
}
