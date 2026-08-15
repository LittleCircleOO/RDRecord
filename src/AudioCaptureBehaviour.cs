using UnityEngine;

namespace RDRecord.Core;

/// <summary>DSP-chain series tap on the AudioListener (plan section 3).
/// Passes data through untouched - player hears the game normally.</summary>
internal sealed class AudioCaptureBehaviour : MonoBehaviour
{
    private TakeController? _take;
    internal bool Bound => _take != null;

    internal void Bind(TakeController take) => _take = take;

    // IMPORTANT: audio thread - no blocking, no allocation
    private void OnAudioFilterRead(float[] data, int channels)
    {
        _take?.PushAudio(data, channels);
    }
}
