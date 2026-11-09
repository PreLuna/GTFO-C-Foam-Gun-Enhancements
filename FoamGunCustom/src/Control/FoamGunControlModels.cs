using System;
using System.Collections.Generic;

namespace PreLuna.FoamGunCustom.Control
{
    public enum FoamGunControlStatus
    {
        Available,
        Uninitialized,
        Missing,
        Incompatible
    }

    [Flags]
    public enum FoamGunControlCapabilities
    {
        None = 0,
        Validate = 1,
        SavePersistent = 2
    }

    public enum FoamGunControlOutcome
    {
        Success,
        NoChanges,
        ValidationRejected,
        StateConflict,
        Unavailable,
        SaveFailed
    }

    public enum FoamGunControlKind
    {
        Boolean,
        Number,
        Integer
    }

    public sealed class FoamGunControlValue
    {
        FoamGunControlValue(FoamGunControlKind kind, object raw)
        {
            Kind = kind;
            Raw = raw;
        }

        public FoamGunControlKind Kind { get; }
        public object Raw { get; }

        public static FoamGunControlValue Boolean(bool value) => new FoamGunControlValue(FoamGunControlKind.Boolean, value);
        public static FoamGunControlValue Number(double value) => new FoamGunControlValue(FoamGunControlKind.Number, value);
        public static FoamGunControlValue Integer(int value) => new FoamGunControlValue(FoamGunControlKind.Integer, value);

        public bool TryGetBoolean(out bool value)
        {
            if (Kind == FoamGunControlKind.Boolean && Raw is bool typed)
            {
                value = typed;
                return true;
            }

            value = false;
            return false;
        }

        public bool TryGetNumber(out double value)
        {
            if (Kind == FoamGunControlKind.Number && Raw is double typed)
            {
                value = typed;
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryGetInteger(out int value)
        {
            if (Kind == FoamGunControlKind.Integer && Raw is int typed)
            {
                value = typed;
                return true;
            }

            value = 0;
            return false;
        }

        public bool ValueEquals(FoamGunControlValue other)
        {
            if (other == null || Kind != other.Kind || Raw == null || other.Raw == null) return false;
            if (Kind == FoamGunControlKind.Number)
                return Raw is double left && other.Raw is double right && Math.Abs(left - right) < 1e-9;
            return Raw.Equals(other.Raw);
        }

        public override string ToString() => Raw?.ToString() ?? string.Empty;
    }

    public sealed class FoamGunControlSetting
    {
        public FoamGunControlSetting(
            string key,
            string displayName,
            FoamGunControlKind kind,
            FoamGunControlValue defaultValue,
            string section,
            string configKey,
            double? minimum = null,
            double? maximum = null)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            DisplayName = displayName ?? key;
            Kind = kind;
            DefaultValue = defaultValue;
            Section = section ?? string.Empty;
            ConfigKey = configKey ?? string.Empty;
            Minimum = minimum;
            Maximum = maximum;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public FoamGunControlKind Kind { get; }
        public FoamGunControlValue DefaultValue { get; }
        public string Section { get; }
        public string ConfigKey { get; }
        public double? Minimum { get; }
        public double? Maximum { get; }

        public FoamGunControlIssue Validate(FoamGunControlValue value)
        {
            if (value == null) return new FoamGunControlIssue(Key, "缺少值");
            if (value.Kind != Kind) return new FoamGunControlIssue(Key, "值类型不匹配");

            if (Kind == FoamGunControlKind.Boolean)
                return value.TryGetBoolean(out _) ? null : new FoamGunControlIssue(Key, "不是合法布尔值");

            if (Kind == FoamGunControlKind.Integer)
            {
                if (!value.TryGetInteger(out var integer)) return new FoamGunControlIssue(Key, "不是合法整数");
                return CheckRange(integer);
            }

            if (!value.TryGetNumber(out var number) || double.IsNaN(number) || double.IsInfinity(number))
                return new FoamGunControlIssue(Key, "不是合法数值");
            return CheckRange(number);
        }

        FoamGunControlIssue CheckRange(double value)
        {
            if (Minimum.HasValue && value < Minimum.Value) return new FoamGunControlIssue(Key, "小于最小值 " + Minimum.Value);
            if (Maximum.HasValue && value > Maximum.Value) return new FoamGunControlIssue(Key, "大于最大值 " + Maximum.Value);
            return null;
        }
    }

    public sealed class FoamGunControlIssue
    {
        public FoamGunControlIssue(string key, string message)
        {
            Key = key ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public string Key { get; }
        public string Message { get; }
    }

    public sealed class FoamGunControlSnapshot
    {
        public FoamGunControlSnapshot(
            string targetId,
            string displayName,
            string version,
            FoamGunControlStatus status,
            string revision,
            FoamGunControlCapabilities capabilities,
            IReadOnlyList<FoamGunControlSetting> settings,
            IDictionary<string, FoamGunControlValue> saved,
            IDictionary<string, FoamGunControlValue> effective)
        {
            TargetId = targetId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Version = version ?? string.Empty;
            Status = status;
            Revision = revision ?? string.Empty;
            Capabilities = capabilities;
            Settings = settings ?? Array.Empty<FoamGunControlSetting>();
            Saved = Copy(saved);
            Effective = Copy(effective);
        }

        public string TargetId { get; }
        public string DisplayName { get; }
        public string Version { get; }
        public FoamGunControlStatus Status { get; }
        public string Revision { get; }
        public FoamGunControlCapabilities Capabilities { get; }
        public IReadOnlyList<FoamGunControlSetting> Settings { get; }
        public IReadOnlyDictionary<string, FoamGunControlValue> Saved { get; }
        public IReadOnlyDictionary<string, FoamGunControlValue> Effective { get; }

        static IReadOnlyDictionary<string, FoamGunControlValue> Copy(IDictionary<string, FoamGunControlValue> source)
        {
            var copy = new Dictionary<string, FoamGunControlValue>(StringComparer.Ordinal);
            if (source != null)
                foreach (var pair in source) copy[pair.Key] = pair.Value;
            return copy;
        }
    }

    public sealed class FoamGunControlChange
    {
        public FoamGunControlChange(string expectedRevision, IDictionary<string, FoamGunControlValue> values)
        {
            ExpectedRevision = expectedRevision ?? string.Empty;
            var copy = new Dictionary<string, FoamGunControlValue>(StringComparer.Ordinal);
            if (values != null)
                foreach (var pair in values) copy[pair.Key] = pair.Value;
            Values = copy;
        }

        public string ExpectedRevision { get; }
        public IReadOnlyDictionary<string, FoamGunControlValue> Values { get; }
    }

    public sealed class FoamGunControlResult
    {
        public FoamGunControlResult(
            FoamGunControlOutcome outcome,
            string message,
            IReadOnlyList<FoamGunControlIssue> issues = null,
            FoamGunControlSnapshot snapshot = null)
        {
            Outcome = outcome;
            Message = message ?? string.Empty;
            Issues = issues ?? Array.Empty<FoamGunControlIssue>();
            Snapshot = snapshot;
        }

        public FoamGunControlOutcome Outcome { get; }
        public string Message { get; }
        public IReadOnlyList<FoamGunControlIssue> Issues { get; }
        public FoamGunControlSnapshot Snapshot { get; }
    }
}
