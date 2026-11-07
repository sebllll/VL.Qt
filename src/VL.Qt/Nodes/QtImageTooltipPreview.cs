using VL.Core;
using VL.Core.Import;
using VL.Lib.Basics.Imaging;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Preview of a QtImage for the tooltip of the vvvv editor (VL.Qt.HDE.vl): the small copy (at most 256 px) the decode
    /// worker made while loading, without touching the GPU. Only images loaded while the editor runs have one.
    /// </summary>
    [ProcessNode(Name = "TooltipPreview", Category = Categories.Qt), Smell(SymbolSmell.Advanced)]
    public sealed class QtImageTooltipPreview
    {
        readonly ResourceClient _client;
        QtImage? _image;
        int _version = -1;
        IImage? _preview;

        public QtImageTooltipPreview(NodeContext nodeContext)
        {
            _client = ResourceClient.ForApp(nodeContext.AppHost);
        }

        /// <summary>For tests: a client on a fake API.</summary>
        internal QtImageTooltipPreview(ResourceClient client)
        {
            _client = client;
        }

        /// <summary>Returns the preview of Image once it is Ready, read once per image and cached.</summary>
        /// <param name="image">The image to preview.</param>
        /// <param name="text">QtImage.ToString(): size and state; the fallback while there is no preview.</param>
        /// <returns>The preview (BGRA8 premultiplied, at most 256 px); null while loading, failed or without preview.</returns>
        [return: Pin(Name = "Image")]
        public IImage? Update(QtImage? image, out string text)
        {
            text = image?.ToString() ?? string.Empty;
            if (image is null || image.State != QtImageState.Ready)
            {
                _image = image;
                _version = -1;
                _preview = null;
                return null;
            }
            // Read once per image and finished load: a new instance, the Ready transition, or a reload of the same resource.
            var version = image.Resource.Version;
            if (!ReferenceEquals(image, _image) || version != _version)
            {
                _image = image;
                _version = version;
                _preview = _client.ReadPreview(image.Resource);
            }
            return _preview;
        }
    }
}
