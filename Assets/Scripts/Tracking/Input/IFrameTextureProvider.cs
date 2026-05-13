using UnityEngine;

public interface IFrameTextureProvider
{
    bool TryGetPreviewTexture(out Texture texture);
    bool TryGetAlignedPreviewTexture(out Texture texture);
}
