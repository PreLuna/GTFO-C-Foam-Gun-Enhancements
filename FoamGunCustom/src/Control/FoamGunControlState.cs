using System;
using System.Collections.Generic;

namespace PreLuna.FoamGunCustom.Control
{
    internal sealed class FoamGunControlState
    {
        internal const string TargetId = "PreLuna.FoamGunCustom";
        internal const string ControlVersion = "1.0.2";

        internal static readonly IReadOnlyList<FoamGunControlSetting> Definitions = new[]
        {
            new FoamGunControlSetting("enabled", "启用", FoamGunControlKind.Boolean, FoamGunControlValue.Boolean(true), "General", "Enabled"),
            new FoamGunControlSetting("shotInterval", "发射间隔", FoamGunControlKind.Number, FoamGunControlValue.Number(.08), "Firing", "ShotInterval", .02, 2),
            new FoamGunControlSetting("launchForce", "发射力度", FoamGunControlKind.Number, FoamGunControlValue.Number(32), "Firing", "LaunchForce", 1, 100),
            new FoamGunControlSetting("pelletSpread", "散布", FoamGunControlKind.Number, FoamGunControlValue.Number(1), "Firing", "PelletSpread", 1, 45),
            new FoamGunControlSetting("ammoCost", "每发资源消耗", FoamGunControlKind.Number, FoamGunControlValue.Number(.5), "Ammo", "CostPerPellet", .01, 50)
        };

        readonly string path;
        readonly Func<string, FoamGunControlValue> readSaved;
        readonly Action<string, FoamGunControlValue> writeSaved;
        readonly Func<string, string> serialize;
        readonly Func<bool> getAutoSave;
        readonly Action<bool> setAutoSave;
        readonly Dictionary<string, FoamGunControlValue> saved = new(StringComparer.Ordinal);
        readonly Dictionary<string, FoamGunControlValue> effective = new(StringComparer.Ordinal);
        string revision = "1";
        string fileHash;

        internal FoamGunControlState(
            string path,
            Func<string, FoamGunControlValue> readSaved,
            Action<string, FoamGunControlValue> writeSaved,
            Func<string, string> serialize,
            Func<bool> getAutoSave = null,
            Action<bool> setAutoSave = null)
        {
            this.path = path ?? throw new ArgumentNullException(nameof(path));
            this.readSaved = readSaved ?? throw new ArgumentNullException(nameof(readSaved));
            this.writeSaved = writeSaved ?? throw new ArgumentNullException(nameof(writeSaved));
            this.serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
            this.getAutoSave = getAutoSave;
            this.setAutoSave = setAutoSave;
            fileHash = FoamGunControlFileStore.Capture(path);

            foreach (var definition in Definitions)
            {
                var value = this.readSaved(definition.Key) ?? definition.DefaultValue;
                saved[definition.Key] = value;
                effective[definition.Key] = value;
            }
        }

        internal string Revision => revision;

        internal FoamGunControlSnapshot ReadSnapshot()
        {
            RefreshSaved();
            return Snapshot();
        }

        internal FoamGunControlResult Validate(FoamGunControlChange change)
        {
            var check = Check(change);
            if (check != null) return check;
            return new FoamGunControlResult(FoamGunControlOutcome.NoChanges, "没有改动", null, Snapshot());
        }

        internal FoamGunControlResult SetPersistent(FoamGunControlChange change)
        {
            var check = Check(change, requireChanges: true);
            if (check != null) return check;

            var previous = SnapshotValues(change.Values.Keys);
            var previousAutoSave = getAutoSave?.Invoke();
            var restoreAutoSave = setAutoSave != null && previousAutoSave.HasValue;
            if (restoreAutoSave) setAutoSave(false);

            try
            {
                try
                {
                    foreach (var pair in change.Values) writeSaved(pair.Key, pair.Value);
                    var writes = new List<FoamGunControlWrite>();
                    foreach (var pair in change.Values)
                    {
                        var definition = FindDefinition(pair.Key);
                        var text = serialize(pair.Key);
                        if (definition == null || text == null)
                            throw new InvalidOperationException("缺少配置映射：" + pair.Key);
                        writes.Add(new FoamGunControlWrite(definition.Section, definition.ConfigKey, text));
                    }

                    fileHash = FoamGunControlFileStore.Save(path, fileHash, writes);
                }
                catch (FoamGunControlConflictException error)
                {
                    RestoreValues(previous);
                    RefreshSaved(false);
                    return new FoamGunControlResult(FoamGunControlOutcome.StateConflict, "配置文件已被外部修改：" + error.Message, null, Snapshot());
                }
                catch (Exception error)
                {
                    RestoreValues(previous);
                    RefreshSaved(false);
                    return new FoamGunControlResult(FoamGunControlOutcome.SaveFailed, "保存失败：" + error.Message, null, Snapshot());
                }

                foreach (var pair in change.Values)
                {
                    saved[pair.Key] = pair.Value;
                    effective[pair.Key] = pair.Value;
                }

                BumpRevision();
                return new FoamGunControlResult(FoamGunControlOutcome.Success, "已保存并生效", null, Snapshot());
            }
            finally
            {
                if (restoreAutoSave) setAutoSave(previousAutoSave.Value);
            }
        }

        FoamGunControlResult Check(FoamGunControlChange change, bool requireChanges = false)
        {
            if (change == null)
                return new FoamGunControlResult(FoamGunControlOutcome.ValidationRejected, "缺少变更集", new[] { new FoamGunControlIssue(string.Empty, "null change") }, Snapshot());
            if (!string.Equals(change.ExpectedRevision, revision, StringComparison.Ordinal))
                return new FoamGunControlResult(FoamGunControlOutcome.StateConflict, "目标状态已变化，请刷新后重试", null, Snapshot());
            if (requireChanges && change.Values.Count == 0)
                return new FoamGunControlResult(FoamGunControlOutcome.NoChanges, "没有改动", null, Snapshot());

            var issues = new List<FoamGunControlIssue>();
            foreach (var pair in change.Values)
            {
                var definition = FindDefinition(pair.Key);
                if (definition == null)
                {
                    issues.Add(new FoamGunControlIssue(pair.Key, "未注册的设置"));
                    continue;
                }

                var issue = definition.Validate(pair.Value);
                if (issue != null) issues.Add(issue);
            }

            if (issues.Count == 0) return null;
            return new FoamGunControlResult(FoamGunControlOutcome.ValidationRejected, "变更未通过校验", issues, Snapshot());
        }

        Dictionary<string, FoamGunControlValue> SnapshotValues(IEnumerable<string> keys)
        {
            var values = new Dictionary<string, FoamGunControlValue>(StringComparer.Ordinal);
            foreach (var key in keys)
                if (saved.TryGetValue(key, out var value)) values[key] = value;
            return values;
        }

        void RestoreValues(Dictionary<string, FoamGunControlValue> values)
        {
            foreach (var pair in values)
            {
                try { writeSaved(pair.Key, pair.Value); }
                catch { }
            }
        }

        void RefreshSaved(bool adoptFile = true)
        {
            var changed = false;
            foreach (var definition in Definitions)
            {
                var value = readSaved(definition.Key) ?? definition.DefaultValue;
                if (!saved.TryGetValue(definition.Key, out var previous) || !previous.ValueEquals(value))
                {
                    saved[definition.Key] = value;
                    effective[definition.Key] = value;
                    changed = true;
                }
            }

            if (changed)
            {
                // Hotkeys (F9/F10 etc.) update ConfigEntry values and let BepInEx save the file.
                // Adopt that internal file state so a subsequent ModControl write is not a false conflict.
                if (adoptFile)
                {
                    var current = FoamGunControlFileStore.Capture(path);
                    if (!string.IsNullOrEmpty(current)) fileHash = current;
                }

                BumpRevision();
            }
        }

        void BumpRevision()
        {
            var number = 1;
            if (int.TryParse(revision, out var parsed)) number = parsed + 1;
            revision = number.ToString();
        }

        FoamGunControlSnapshot Snapshot()
            => new FoamGunControlSnapshot(
                TargetId,
                "PreLuna 泡沫枪",
                ControlVersion,
                FoamGunControlStatus.Available,
                revision,
                FoamGunControlCapabilities.Validate | FoamGunControlCapabilities.SavePersistent,
                Definitions,
                saved,
                effective);

        internal static FoamGunControlSnapshot Unavailable(FoamGunControlStatus status)
            => new FoamGunControlSnapshot(
                TargetId,
                "PreLuna 泡沫枪",
                string.Empty,
                status,
                string.Empty,
                FoamGunControlCapabilities.None,
                Array.Empty<FoamGunControlSetting>(),
                new Dictionary<string, FoamGunControlValue>(),
                new Dictionary<string, FoamGunControlValue>());

        static FoamGunControlSetting FindDefinition(string key)
        {
            foreach (var definition in Definitions)
                if (string.Equals(definition.Key, key, StringComparison.Ordinal)) return definition;
            return null;
        }
    }
}
