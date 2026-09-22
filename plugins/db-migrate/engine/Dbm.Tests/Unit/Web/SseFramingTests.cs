using Dbm.Web;
using Dbm.Web.Endpoints;

namespace Dbm.Tests.Unit.Web;

public class SseFramingTests
{
    /// <summary>
    /// Open item 3.5: <c>WriteEventAsync</c> replaced <c>\n</c> in the data line but not a bare <c>\r</c>, and the
    /// SSE spec treats a bare CR as a line terminator too - so a CR would end the <c>data:</c> line early and the
    /// remainder would be parsed as a new (unprefixed, malformed) field. Unreachable today only because
    /// <c>m.Data</c> is always JSON, which escapes control characters inside string values; the existing <c>\n</c>
    /// guard shows the author expected untrusted text here. <b>Harm:</b> a future non-JSON payload, or JSON produced
    /// by a differently configured serializer, containing a raw CR would corrupt the SSE stream: the EventSource
    /// client would parse a truncated frame and drop everything after the CR.
    /// </summary>
    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a\rb", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\r\n\r\nb", "a  b")]
    public void FrameEvent_replaces_bare_CR_as_well_as_LF_in_the_data_line(string data, string expectedData)
    {
        var framed = CoreEndpoints.FrameEvent(new SseMessage(1, "test", data));

        Assert.Equal($"id: 1\nevent: test\ndata: {expectedData}\n\n", framed);
    }

    [Fact]
    public void FrameEvent_omits_the_id_line_when_the_message_has_none()
    {
        var framed = CoreEndpoints.FrameEvent(new SseMessage(null, "ping", "ok"));

        Assert.Equal("event: ping\ndata: ok\n\n", framed);
    }
}
