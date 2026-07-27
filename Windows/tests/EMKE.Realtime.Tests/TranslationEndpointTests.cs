using EMKE.Core;

namespace EMKE.Realtime.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class TranslationEndpointTests
{
    [TestMethod]
    [DataRow("https://api.example.test", "wss://api.example.test/realtime/translations?model=gpt-realtime")]
    [DataRow("HTTPS://api.example.test", "wss://api.example.test/realtime/translations?model=gpt-realtime")]
    [DataRow("wss://api.example.test", "wss://api.example.test/realtime/translations?model=gpt-realtime")]
    [DataRow("WSS://api.example.test", "wss://api.example.test/realtime/translations?model=gpt-realtime")]
    [DataRow("https://api.example.test/v1", "wss://api.example.test/v1/realtime/translations?model=gpt-realtime")]
    [DataRow("https://api.example.test/v1///", "wss://api.example.test/v1/realtime/translations?model=gpt-realtime")]
    public void CreateNormalizesSupportedBaseUrls(string baseAddress, string expected)
    {
        TranslationEndpointResult result = TranslationEndpoint.Create(baseAddress, "gpt-realtime");

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Error);
        Assert.AreEqual(expected, result.Endpoint!.AbsoluteUri);
    }

    [TestMethod]
    public void CreateUriEncodesTheModelWithoutChangingTheBasePath()
    {
        TranslationEndpointResult result = TranslationEndpoint.Create(
            "https://api.example.test/base%20path/",
            "model alpha/β+1");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(
            "wss://api.example.test/base%20path/realtime/translations?model=model%20alpha%2F%CE%B2%2B1",
            result.Endpoint!.AbsoluteUri);
    }

    [TestMethod]
    [DataRow("http://api.example.test")]
    [DataRow("ws://api.example.test")]
    [DataRow("file:///tmp/socket")]
    [DataRow("/relative")]
    [DataRow("https:///missing-host")]
    [DataRow("")]
    [DataRow("not a uri")]
    public void CreateRejectsUnsupportedOrNonAbsoluteBaseUrlsWithTypedError(string baseAddress)
    {
        TranslationEndpointResult result = TranslationEndpoint.Create(baseAddress, "model");

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Endpoint);
        Assert.AreEqual(ErrorCategory.Configuration, result.Error!.Category);
        Assert.AreEqual("translationEndpoint.invalidBaseUrl", result.Error.Code);
        Assert.HasCount(0, result.Error.Parameters);
        Assert.AreEqual(RecoveryAction.EditSettings, result.Error.RecoveryAction);
    }

    [TestMethod]
    [DataRow("https://api.example.test?region=one")]
    [DataRow("https://api.example.test?")]
    [DataRow("https://api.example.test/#fragment")]
    [DataRow("https://api.example.test/#")]
    [DataRow("https://api.example.test/base?region=one#fragment")]
    public void CreateRejectsExistingQueryOrFragment(string baseAddress)
    {
        TranslationEndpointResult result = TranslationEndpoint.Create(baseAddress, "model");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("translationEndpoint.ambiguousBaseUrl", result.Error!.Code);
    }

    [TestMethod]
    public void CreateRejectsUserInformationWithoutEchoingIt()
    {
        TranslationEndpointResult result = TranslationEndpoint.Create(
            "https://private-user:private-password@api.example.test",
            "model");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("translationEndpoint.invalidBaseUrl", result.Error!.Code);
        Assert.HasCount(0, result.Error.Parameters);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    public void CreateRejectsEmptyModelWithTypedError(string model)
    {
        TranslationEndpointResult result = TranslationEndpoint.Create(
            "https://api.example.test",
            model);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("translationEndpoint.invalidModel", result.Error!.Code);
    }
}

#pragma warning restore CA1515
