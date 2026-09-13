// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using JPSoftworks.MediaControlsExtension.Media.Vlc;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class VlcConnectionOptionsTests
{
    [TestMethod]
    [DataRow("http://127.0.0.1:8080", true)]
    [DataRow("http://127.0.0.2:8090", true)]
    [DataRow("http://localhost:8080", true)]
    [DataRow("http://[::1]:8080", true)]
    [DataRow("https://other-pc:8443", false)]
    [DataRow("http://192.168.1.10:8080", false)]
    [DataRow("http://[2001:db8::1]:8080", false)]
    public void AutomaticTreatmentUsesExplicitLoopbackOnly(string endpoint, bool local)
    {
        var options = new VlcConnectionOptions { Endpoint = endpoint };
        Assert.IsNotNull(options.BaseAddress);
        Assert.AreEqual(local, options.IsLocalConnection);
        Assert.AreEqual(local, options.TreatAsLocal);
        Assert.AreEqual(local, (options with { TreatAsLocal = !local }).IsLocalConnection);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("other-pc:8080")]
    [DataRow("file:///C:/VLC")]
    [DataRow("ftp://other-pc")]
    [DataRow("http://other-pc:0")]
    [DataRow("http://other-pc:65536")]
    [DataRow("http://other-pc/requests/status.json")]
    [DataRow("http://user:secret@other-pc:8080")]
    [DataRow("http://other-pc:8080/?password=secret")]
    [DataRow("http://other-pc:8080/#secret")]
    public void InvalidEndpointsAreRejectedWithoutEchoingCredentials(string endpoint)
    {
        var options = new VlcConnectionOptions(Password: "secret") { Endpoint = endpoint };
        Assert.IsNull(options.BaseAddress);
        Assert.IsFalse(options.IsLocalConnection);
        Assert.DoesNotContain("secret", options.ToString());
    }
}