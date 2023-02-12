using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Stacks;
using perftools.profiles;
using ProtoBuf;

namespace DotNet.Monitor.PProf.Api;

public class Converter
{
    public static class Const
    {
        public static readonly string SAMPLES = "samples";
        public static readonly string COUNT = "count";
        public static readonly string CPU = "cpu";
        public static readonly string NANOSECONDS = "nanoseconds";
        public static readonly string THREAD = "thread";

        public static readonly string UNKNOWN = "--UNKNOWN--";

        public static readonly string CPU_TIME = "CPU_TIME";
        public static readonly string UNMANAGED_CODE_TIME = "UNMANAGED_CODE_TIME";

        public static readonly string[] All
            = new[] { Const.SAMPLES, Const.COUNT, Const.CPU, Const.NANOSECONDS, Const.THREAD, Const.UNKNOWN, Const.CPU_TIME, Const.UNMANAGED_CODE_TIME };

        public static readonly string[] FilteredStackFrameNames
            = new[] { Const.CPU_TIME, Const.UNMANAGED_CODE_TIME };
    }

    public record CallStackSample(StackSourceCallStackIndex Index, long Nanoseconds, double Time)
    {
        public CallStackSample(StackSourceSample sample)
            : this(sample.StackIndex, (long)Math.Round((double)sample.Metric * 1_000_000), sample.TimeRelativeMSec)
        { }
    }

    public record CumulativeSample(StackSourceCallStackIndex Index, long Nanoseconds, long Count)
    {
        public CumulativeSample(CallStackSample sample)
            : this(sample.Index, sample.Nanoseconds, 1)
        { }

        public CumulativeSample Add(CallStackSample sample)
        {
            Debug.Assert(sample.Index == Index);
            return this with { Index = Index, Nanoseconds = Nanoseconds + sample.Nanoseconds, Count = Count + 1 };
        }

        public bool TryAdd(CallStackSample sample, out CumulativeSample combined)
        {
            if (sample.Index == Index)
            {
                combined = this.Add(sample);
                return true;
            }

            combined = this;
            return false;
        }
    }

    private readonly Profile _profile = new Profile();

    private readonly Dictionary<string, long> _strings = new Dictionary<string, long>();
    private readonly Dictionary<StackSourceFrameIndex, ulong> _functions = new Dictionary<StackSourceFrameIndex, ulong>();

    public Converter(StackSource source)
    {
        // From profile.proto, lines 65-66
        //
        // // A common table for strings referenced by various messages.
        // // string_table[0] must always be "".
        _profile.StringTables.Add("");
        _strings[""] = 0;

        // Populate all constant strings
        for (var i = 0; i < Const.All.Length; i++)
        {
            _ = TryGet(Const.All[i]);
        }

        // Configure constant pprof profile fields
        _profile.PeriodType = new perftools.profiles.ValueType { Type = _strings[Const.CPU], Unit = _strings[Const.NANOSECONDS] };
        _profile.Period = 1_000_000_000;

        _profile.SampleTypes.AddRange(new[]
        {
                new perftools.profiles.ValueType { Type = _strings[Const.SAMPLES], Unit = _strings[Const.COUNT] },
                new perftools.profiles.ValueType { Type = _strings[Const.CPU], Unit = _strings[Const.NANOSECONDS] },
            });

        // Retrieve sample information from the StackSource
        var samples = new List<CallStackSample>();
        source.ForEach(sample => samples.Add(new CallStackSample(sample)));

        // Combine matching stack frames and convert into pprof samples
        CumulativeSample? cumulativeSample = null;
        foreach (var stackSample in samples.OrderBy(sample => sample.Time))
        {
            if (cumulativeSample is null)
            {
                cumulativeSample = new CumulativeSample(stackSample);
                continue;
            }
            else if (cumulativeSample.TryAdd(stackSample, out var combinedSample))
            {
                cumulativeSample = combinedSample;
                continue;
            }

            var sample = ConstructSample(source, cumulativeSample);
            _profile.Samples.Add(sample);

            cumulativeSample = new CumulativeSample(stackSample);
        }
    }

    public void Serialize(Stream stream)
        => Serializer.Serialize(stream, _profile);

    private long TryGet(string str)
    {
        if (!_strings.ContainsKey(str))
        {
            _strings.Add(str, _profile.StringTables.Count);
            _profile.StringTables.Add(str);
        }

        return _strings[str];
    }

    private ulong TryGet(StackSource stackSource, StackSourceFrameIndex index)
    {
        if (!_functions.ContainsKey(index))
        {
            var function = new Function
            {
                Id = (ulong)_profile.Functions.Count + 1,
                Name = TryGet(stackSource.GetFrameName(index, false)),
            };

            var location = new Location { Id = function.Id };
            location.Lines.Add(new Line { FunctionId = function.Id });

            _functions.Add(index, function.Id);

            _profile.Functions.Add(function);
            _profile.Locations.Add(location);
        }

        return _functions[index];
    }

    private Sample ConstructSample(StackSource stackSource, CumulativeSample cumulativeSample)
    {
        var sample = new Sample();
        var functions = new Dictionary<StackSourceFrameIndex, ulong>();

        var stackIndex = cumulativeSample.Index;
        var locationIds = new List<ulong>();

        while (stackIndex != StackSourceCallStackIndex.Invalid)
        {
            var frameIndex = stackSource.GetFrameIndex(stackIndex);
            var frameName = stackSource.GetFrameName(frameIndex, false);

            // Drop frames containing explicitly filtered names (eg. CPU_TIME and UNMANAGED_CODE_TIME)
            //
            // These frames are dropped because they are (almost?) always the leaf frame of a stack and
            // cause the "self" time metrics to be calculated incorrectly (all time is allocated to, eg., CPU_TIME
            // instead of the actual method).
            //
            // Some more information can be found here...
            // https://github.com/dotnet/diagnostics/issues/1166
            // https://github.com/microsoft/perfview/pull/1613
            if (!Const.FilteredStackFrameNames.Contains(frameName))
            {
                locationIds.Add(TryGet(stackSource, frameIndex));
            }

            stackIndex = stackSource.GetCallerIndex(stackIndex);
        }

        sample.LocationIds = locationIds.ToArray();

        var threadName = FindThreadName(stackSource, cumulativeSample.Index);
        sample.Labels.Add(new Label { Key = TryGet(Const.THREAD), Str = TryGet(threadName) });

        sample.Values = new[] { cumulativeSample.Count, cumulativeSample.Nanoseconds };

        return sample;
    }

    private string FindThreadName(StackSource stackSource, StackSourceCallStackIndex index)
    {
        var previousName = Const.UNKNOWN;
        while (index != StackSourceCallStackIndex.Invalid)
        {
            var frameIndex = stackSource.GetFrameIndex(index);
            var name = stackSource.GetFrameName(frameIndex, false);

            if (name == "Threads") { return previousName; }

            previousName = name;
            index = stackSource.GetCallerIndex(index);
        }

        return Const.UNKNOWN;
    }
}
