using RemoteTranscode.Common;

namespace RemoteTranscode.Tests;

public class ValidatorHardeningTests
{
    private static string[] WithOption(string option, string value) =>
        ["-i", "{input:0}", "-c:v:0", "libx264", option, value, "-f", "segment", "-segment_list", "{list}", "{out}"];

    private static string[] WithFilter(string graph) =>
        ["-i", "{input:0}", "-filter_complex", graph, "-c:v:0", "libx264", "-f", "segment", "-segment_list", "{list}", "{out}"];

    [Theory]
    [InlineData("-x264-params:v:0", "keyint=72:stats=C:/x")]
    [InlineData("-x264-params:v:0", "stats=out.stats")]
    [InlineData("-x264opts:v:0", "subme=0:dump-yuv=/tmp/y")]
    [InlineData("-x265-params:v:0", "csv=/tmp/x.csv")]
    [InlineData("-x265-params:v:0", "analysis-save=a.dat")]
    [InlineData("-x265-params:v:0", "keyint=72:recon=..\\x")]
    [InlineData("-segment_format_options", "hls_segment_filename=..\\..\\x")]
    [InlineData("-segment_format_options", "mpegts_flags=/x")]
    public void Encoder_and_muxer_parameters_cannot_name_files(string option, string value) =>
        Assert.False(ArgTemplateValidator.Validate(WithOption(option, value), out _));

    [Theory]
    [InlineData("-x264opts:v:0", "subme=0:me_range=4:rc_lookahead=10:partitions=none")]
    [InlineData("-x264-params:v:0", "keyint=72:min-keyint=72:partitions=i4x4,p8x8:sar=1/1")]
    [InlineData("-x265-params:v:0", "keyint=72:min-keyint=72:no-info=1:fps=24000/1001")]
    [InlineData("-segment_format_options", "mpegts_copyts=1")]
    public void Real_encoder_parameters_stay_valid(string option, string value) =>
        Assert.True(ArgTemplateValidator.Validate(WithOption(option, value), out var error), error);

    [Theory]
    [InlineData("[0:0]subtitles=/etc/passwd[o]")]
    [InlineData("[0:0]subtitles=f=/etc/passwd[o]")]
    [InlineData("[0:0]subtitles='C\\:/x'[o]")]
    [InlineData("[0:0]subtitles=x.srt[o]")]
    [InlineData("[0:0]subtitles=filename={aux:0}:fontsdir=/usr/share/fonts[o]")]
    [InlineData("[0:0]subtitles={aux:0}:original_size=hd720:/etc/fonts[o]")]
    [InlineData("[0:0]ass=a.ass[o]")]
    [InlineData("[0:0]scale=w=720:h=388,movie =/etc/passwd[o]")]
    [InlineData("[0:0]scale=w=720, movie@m=/etc/passwd [o]")]
    [InlineData("[0:0]scale=w=720;amovie=x.wav[a]")]
    [InlineData("[0:0]subtitles=filename='{aux:0}'\\,movie=x[o]")]
    [InlineData("[0:0]subtitles=filename='../{aux:0}'[o]")]
    [InlineData("[0:0]scale=w=720[o")]
    public void Filter_graphs_cannot_reach_files(string graph) =>
        Assert.False(ArgTemplateValidator.Validate(WithFilter(graph), out _));

    [Theory]
    [InlineData("[0:0]scale@f1=width=720:height=388,subtitles@f2=filename='{aux:1}':fontsdir='{fonts}':charenc=utf-8[f2_out0]")]
    [InlineData("[0:0]subtitles={aux:0}[o]")]
    [InlineData("[0:0]scale=trunc(min(max(iw\\,ih*a)\\,1280)/2)*2:trunc(ow/a/2)*2,format=yuv420p[o]")]
    [InlineData("[0:0]scale@f1=width=426:height=176,setparams@f2=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,format@f3=pix_fmts=p010,supertonemap@f4=tonemap=hable,setparams@f5=color_primaries=bt709:color_trc=bt709:colorspace=bt709,format@f6=pix_fmts=yuv420p[f6_out0]")]
    [InlineData("[0:v] scale = w=1280:h=720 [v]; [0:a] aresample=48000 [a]")]
    public void Emby_filter_graphs_stay_valid(string graph) =>
        Assert.True(ArgTemplateValidator.Validate(WithFilter(graph), out var error), error);

    [Fact]
    public void Filter_names_tolerate_spaces_before_arguments() =>
        Assert.Contains("movie", ArgTemplateValidator.FilterNames("scale=w=1,movie =/x"));
}

public class PlannerParsingTests
{
    [Theory]
    [InlineData("00:00:03.000", 3)]
    [InlineData("00:10:00.000", 600)]
    [InlineData("05:30", 330)]
    [InlineData("01:05:30.5", 3930.5)]
    [InlineData("3", 3)]
    [InlineData("2.5", 2.5)]
    [InlineData("-1.5", -1.5)]
    public void Durations_parse_like_ffmpeg(string value, double expected) =>
        Assert.Equal(expected, EmbyJobPlanner.ParseDuration(value)!.Value, 6);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("1.5:30")]
    public void Unreadable_durations_are_null(string? value) =>
        Assert.Null(EmbyJobPlanner.ParseDuration(value));

    [Theory]
    [InlineData("/volume4/Film/a.mkv", "/volume4/Film", true)]
    [InlineData("/volume4/Film//./a.mkv", "/volume4/Film/", true)]
    [InlineData("/volume4/Film", "/volume4/Film", true)]
    [InlineData("/volume4/Film/../Admin/a.mkv", "/volume4/Film", false)]
    [InlineData("/volume4/Film/sub/../a.mkv", "/volume4/Film", false)]
    [InlineData("/volume4/Films/a.mkv", "/volume4/Film", false)]
    [InlineData("volume4/Film/a.mkv", "/volume4/Film", false)]
    public void Paths_are_checked_against_roots(string path, string root, bool expected) =>
        Assert.Equal(expected, EmbyJobPlanner.IsUnder(path, root));
}
