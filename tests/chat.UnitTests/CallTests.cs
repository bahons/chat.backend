using System.Security.Cryptography;
using System.Text;
using chat.Api.Calls;
using chat.Domain.Calls;
using chat.Infrastructure.Media;
using Microsoft.Extensions.Options;
using Xunit;

namespace chat.UnitTests;

public sealed class CallTests
{
    private static Call NewCall(DateTimeOffset now) => new()
    {
        ConversationId = Guid.NewGuid(),
        ClientUserId = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        InitiatorUserId = Guid.NewGuid(),
        InitiatorFamilyId = Guid.NewGuid(),
        ClientCallId = Guid.NewGuid(),
        InitiatorIsClient = true,
        Kind = CallKind.VIDEO,
        CreatedAt = now,
        ExpiresAt = now.AddSeconds(45)
    };

    [Fact]
    public void StateMachineRequiresAcceptanceAndOfferBeforeAnswerAndHasTerminalStates()
    {
        var now = DateTimeOffset.UtcNow;
        var call = NewCall(now);
        Assert.False(call.Answer(now));
        Assert.False(call.Offer(now));
        Assert.True(call.Accept(now));
        Assert.False(call.Accept(now));
        Assert.False(call.Answer(now));
        Assert.True(call.Offer(now));
        Assert.True(call.Answer(now.AddSeconds(1)));
        Assert.Equal(CallState.ACTIVE, call.State);
        Assert.True(call.Offer(now.AddSeconds(2)));
        Assert.Equal(CallState.CONNECTING, call.State);
        Assert.True(call.Answer(now.AddSeconds(3)));
        Assert.True(call.Finish(now.AddSeconds(4), CallState.ENDED, "HANGUP"));
        Assert.False(call.Finish(now.AddSeconds(5), CallState.REJECTED, "DECLINED"));
        Assert.False(call.Accept(now.AddSeconds(5)));
    }

    [Fact]
    public void RingingAndConnectingExpireAndCannotBeResurrected()
    {
        var now = DateTimeOffset.UtcNow;
        var ringing = NewCall(now);
        Assert.True(ringing.Expire(now.AddSeconds(45)));
        Assert.Equal(CallState.MISSED, ringing.State);
        Assert.False(ringing.Accept(now.AddSeconds(46)));
        var connecting = NewCall(now);
        Assert.True(connecting.Accept(now));
        Assert.True(connecting.Expire(now.AddSeconds(60)));
        Assert.Equal(CallState.ENDED, connecting.State);
        Assert.Equal("TIMEOUT", connecting.EndedReason);
    }

    [Fact]
    public void TurnCredentialsUseCoturnHmacAndCannotOutliveTheCall()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new CallOptions
        {
            Enabled = true,
            TurnSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            TurnUrls = ["turn:turn.example.test:3478?transport=udp", "turns:turn.example.test:5349?transport=tcp"],
            StunUrls = ["stun:turn.example.test:3478"]
        };
        Assert.True(options.IsConfigured);
        var call = NewCall(now);
        var result = new TurnCredentials(Options.Create(options), TimeProvider.System).Issue(call);
        Assert.True(result.ExpiresAt <= call.ExpiresAt);
        Assert.Equal(Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(options.TurnSecret),
            Encoding.UTF8.GetBytes(result.IceServers[0].Username!))), result.IceServers[0].Credential);
        Assert.Equal(result.ExpiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.IceServers[0].Username!.Split(':')[0]);
        options.TurnSecret = "";
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void MediaProbeRejectsExcessDurationWrongContainerAndAudioWithVideoTracks()
    {
        const string audio = """{"format":{"format_name":"mov,mp4","duration":"2.5"},"streams":[{"codec_type":"audio","codec_name":"aac"}]}""";
        var header = new byte[] { 0, 0, 0, 20, 102, 116, 121, 112, 105, 115, 111, 109 };
        var good = FfprobeMediaProbe.Parse(audio, header, "audio/mp4");
        Assert.Equal(2500, good?.DurationMilliseconds);
        Assert.Null(FfprobeMediaProbe.Parse(audio.Replace("2.5", "301", StringComparison.Ordinal), header, "audio/mp4"));
        Assert.Null(FfprobeMediaProbe.Parse(audio, "%PDF-bad"u8, "audio/mp4"));
        Assert.Null(FfprobeMediaProbe.Parse(audio, header, "video/mp4"));
        const string video = """{"format":{"format_name":"mov,mp4","duration":"2"},"streams":[{"codec_type":"video","codec_name":"h264","width":1280,"height":720}]}""";
        Assert.Null(FfprobeMediaProbe.Parse(video, header, "audio/mp4"));
        Assert.NotNull(FfprobeMediaProbe.Parse(video, header, "video/mp4"));
        Assert.Null(FfprobeMediaProbe.Parse(video.Replace("1280", "8000", StringComparison.Ordinal), header, "video/mp4"));
    }
}

