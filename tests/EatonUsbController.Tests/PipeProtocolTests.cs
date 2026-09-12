using EatonUsbController.Core;
using EatonUsbController.Core.Contracts;

namespace EatonUsbController.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public async Task RoundTrip_PreservesPolymorphicMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var stream = new MemoryStream();
        await PipeProtocol.SendAsync(
            stream,
            new SendNutCommandRequest { Command = "beeper.off" },
            cancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);

        var message = await PipeProtocol.ReceiveAsync(reader, cancellationToken);

        var command = Assert.IsType<SendNutCommandRequest>(message);
        Assert.Equal("beeper.off", command.Command);
    }
}