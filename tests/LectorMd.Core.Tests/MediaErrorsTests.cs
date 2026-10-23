using System.Runtime.InteropServices;
using LectorMd.Core.Audio;

namespace LectorMd.Core.Tests;

public class MediaErrorsTests
{
    [Fact]
    public void AudioDeviceLost_IsRecognizedByItsHResult()
    {
        // 0xC00D11E5: "An audio device was disconnected or reconfigured" (p. ej. al desconectarse Escritorio remoto).
        var error = new COMException("0xC00D11E5", unchecked((int)0xC00D11E5));

        Assert.True(MediaErrors.IsAudioDeviceLost(error));
        Assert.True(MediaErrors.IsAudioDeviceLost(new InvalidOperationException("envuelto", error)));
    }

    [Fact]
    public void OtherMediaErrors_AreNotAudioDeviceLost()
    {
        Assert.False(MediaErrors.IsAudioDeviceLost(new COMException("archivo dañado", unchecked((int)0xC00D11B1))));
        Assert.False(MediaErrors.IsAudioDeviceLost(new IOException("disco")));
    }
}
