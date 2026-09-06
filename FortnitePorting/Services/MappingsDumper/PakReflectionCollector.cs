using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;

namespace FortnitePorting.Services.MappingsDumper;

/// <summary>
/// Collects reflected types from the mounted build. This is the server-side stand-in for
/// UnrealMappingsDumper's <c>ObjObjects::ForEach</c> walk: the dumper runs inside the game and reads
/// every UClass/UScriptStruct/UEnum out of GObjects, whereas here there is no game process, so the
/// same objects are read out of the cooked packages through CUE4Parse.
///
/// What that means in practice: cooked packages carry the reflection of the types they define —
/// Blueprint generated classes, user defined structs and enums — so those come out complete. Native
/// <c>/Script/...</c> types live in the executable, not in the paks, so they are not collected here;
/// <see cref="UsmapSnapshotReader"/> supplies them from an existing .usmap when a dump is merged.
/// </summary>
public sealed class PakReflectionCollector
{
    private readonly IFileProvider _provider;

    public PakReflectionCollector(IFileProvider provider)
    {
        _provider = provider;
    }

    /// <summary>Export class names worth deserializing; everything else is skipped unread.</summary>
    private static readonly HashSet<string> StructClasses = new(StringComparer.Ordinal)
    {
        "Class", "BlueprintGeneratedClass", "AnimBlueprintGeneratedClass", "WidgetBlueprintGeneratedClass",
        "ControlRigBlueprintGeneratedClass", "DynamicClass", "VerseClass",
        "ScriptStruct", "UserDefinedStruct"
    };

    private static readonly HashSet<string> EnumClasses = new(StringComparer.Ordinal)
    {
        "Enum", "UserDefinedEnum"
    };

    public sealed class Options
    {
        /// <summary>Only scan packages whose virtual path starts with this (case-insensitive).</summary>
        public string? PathFilter;

        /// <summary>Maximum packages to open; 0 scans every matching package.</summary>
        public int MaxPackages = 5000;

        /// <summary>Wall-clock budget for the scan. The dump returns what it has when it runs out.</summary>
        public TimeSpan Timeout = TimeSpan.FromMinutes(2);
    }

    public sealed class Stats
    {
        public int PackagesMatched;
        public int PackagesScanned;
        public int PackagesFailed;
        public int ExportsInspected;
        public int StructsCollected;
        public int EnumsCollected;
        public bool TimedOut;
        public bool LimitReached;
        public double ElapsedSeconds;
    }

    public ReflectionSnapshot Collect(Options options, Stats stats, CancellationToken cancellationToken = default)
    {
        var snapshot = new ReflectionSnapshot();
        var filter = string.IsNullOrWhiteSpace(options.PathFilter) ? null : options.PathFilter.Replace('\\', '/').Trim('/');
        var watch = Stopwatch.StartNew();

        foreach (var (path, file) in _provider.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!file.IsUePackage) continue;
            if (filter != null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            stats.PackagesMatched++;

            if (options.MaxPackages > 0 && stats.PackagesScanned >= options.MaxPackages)
            {
                stats.LimitReached = true;
                break;
            }

            if (watch.Elapsed >= options.Timeout)
            {
                stats.TimedOut = true;
                break;
            }

            IPackage? package;
            try
            {
                if (!_provider.TryLoadPackage(file, out package)) { stats.PackagesFailed++; continue; }
            }
            catch (Exception)
            {
                stats.PackagesFailed++;
                continue;
            }

            stats.PackagesScanned++;
            CollectFromPackage(package, snapshot, stats);
        }

        watch.Stop();
        stats.ElapsedSeconds = watch.Elapsed.TotalSeconds;
        stats.StructsCollected = snapshot.Structs.Count;
        stats.EnumsCollected = snapshot.Enums.Count;
        return snapshot;
    }

    private static void CollectFromPackage(IPackage package, ReflectionSnapshot snapshot, Stats stats)
    {
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            // The export class is resolved from the export map, so packages that define no reflected
            // type are never deserialized — the same "check the class first" shape as the dumper's
            // GObjects loop, just without a live UObject.
            string? className;
            try
            {
                className = package.ResolvePackageIndex(new FPackageIndex(package, i + 1))?.Class?.Name.Text;
            }
            catch (Exception)
            {
                continue;
            }

            if (className == null) continue;

            var isStruct = StructClasses.Contains(className);
            var isEnum = !isStruct && EnumClasses.Contains(className);
            if (!isStruct && !isEnum) continue;

            stats.ExportsInspected++;

            try
            {
                var export = package.GetExport(i);
                switch (export)
                {
                    case UEnum e:
                        snapshot.AddEnum(ConvertEnum(e));
                        break;
                    case UStruct s:
                        snapshot.AddStruct(ConvertStruct(s));
                        break;
                }
            }
            catch (Exception)
            {
                stats.PackagesFailed++;
            }
        }
    }

    private static DumpedEnum ConvertEnum(UEnum e)
    {
        var dumped = new DumpedEnum { Name = e.Name };
        foreach (var (name, value) in e.Names ?? [])
        {
            dumped.Members.Add(new DumpedEnumMember { Name = ShortEnumMember(name.Text), Value = value });
        }
        return dumped;
    }

    private static DumpedStruct ConvertStruct(UStruct s)
    {
        var dumped = new DumpedStruct
        {
            Name = s.Name,
            Super = ResolveName(s.SuperStruct)
        };

        foreach (var field in s.ChildProperties ?? [])
        {
            if (field is not FProperty prop) continue;
            dumped.Properties.Add(new DumpedProperty
            {
                Name = prop.Name.Text,
                ArrayDim = prop.ArrayDim <= 0 ? 1 : prop.ArrayDim,
                Type = ConvertProperty(prop)
            });
        }

        return dumped;
    }

    /// <summary>
    /// Maps a CUE4Parse property to its usmap type, mirroring <c>GetPropertyType</c> plus the
    /// <c>WriteProperty</c> special cases in UnrealMappingsDumper's dumper.cpp: object-like properties
    /// collapse onto ObjectProperty, every multicast delegate flavour onto MulticastDelegateProperty,
    /// and a byte property that carries an enum becomes an EnumProperty with a byte underlying type
    /// (the dumper's EnumAsByteProperty case).
    /// </summary>
    private static DumpedPropertyType ConvertProperty(FProperty prop)
    {
        switch (prop)
        {
            case FEnumProperty e:
                return new DumpedPropertyType
                {
                    Type = EPropertyType.EnumProperty,
                    EnumName = ResolveName(e.Enum),
                    Inner = e.UnderlyingProp != null
                        ? ConvertProperty(e.UnderlyingProp)
                        : DumpedPropertyType.Of(EPropertyType.ByteProperty)
                };

            case FByteProperty b:
                return b.Enum is { IsNull: false }
                    ? new DumpedPropertyType
                    {
                        Type = EPropertyType.EnumProperty,
                        EnumName = ResolveName(b.Enum),
                        Inner = DumpedPropertyType.Of(EPropertyType.ByteProperty)
                    }
                    : DumpedPropertyType.Of(EPropertyType.ByteProperty);

            case FStructProperty s:
                return new DumpedPropertyType { Type = EPropertyType.StructProperty, StructName = ResolveName(s.Struct) };

            case FArrayProperty a:
                return new DumpedPropertyType { Type = EPropertyType.ArrayProperty, Inner = ConvertInner(a.Inner) };

            case FSetProperty set:
                return new DumpedPropertyType { Type = EPropertyType.SetProperty, Inner = ConvertInner(set.ElementProp) };

            case FMapProperty m:
                return new DumpedPropertyType
                {
                    Type = EPropertyType.MapProperty,
                    Inner = ConvertInner(m.KeyProp),
                    Value = ConvertInner(m.ValueProp)
                };

            case FOptionalProperty opt:
                return new DumpedPropertyType { Type = EPropertyType.OptionalProperty, Inner = ConvertInner(opt.ValueProperty) };

            // Object-like properties. The most derived types are matched first because they all
            // inherit FObjectProperty.
            case FSoftClassProperty:
            case FSoftObjectProperty:
                return DumpedPropertyType.Of(EPropertyType.SoftObjectProperty);
            case FWeakObjectProperty:
                return DumpedPropertyType.Of(EPropertyType.WeakObjectProperty);
            case FVerseClassProperty:
            case FClassProperty:
            case FObjectProperty:
                return DumpedPropertyType.Of(EPropertyType.ObjectProperty);

            case FBoolProperty: return DumpedPropertyType.Of(EPropertyType.BoolProperty);
            case FInt8Property: return DumpedPropertyType.Of(EPropertyType.Int8Property);
            case FInt16Property: return DumpedPropertyType.Of(EPropertyType.Int16Property);
            case FIntProperty: return DumpedPropertyType.Of(EPropertyType.IntProperty);
            case FInt64Property: return DumpedPropertyType.Of(EPropertyType.Int64Property);
            case FUInt16Property: return DumpedPropertyType.Of(EPropertyType.UInt16Property);
            case FUInt32Property: return DumpedPropertyType.Of(EPropertyType.UInt32Property);
            case FUInt64Property: return DumpedPropertyType.Of(EPropertyType.UInt64Property);
            case FFloatProperty: return DumpedPropertyType.Of(EPropertyType.FloatProperty);
            case FDoubleProperty: return DumpedPropertyType.Of(EPropertyType.DoubleProperty);
            case FNameProperty: return DumpedPropertyType.Of(EPropertyType.NameProperty);
            case FUtf8StrProperty: return DumpedPropertyType.Of(EPropertyType.Utf8StrProperty);
            case FStrProperty: return DumpedPropertyType.Of(EPropertyType.StrProperty);
            case FTextProperty: return DumpedPropertyType.Of(EPropertyType.TextProperty);
            case FInterfaceProperty: return DumpedPropertyType.Of(EPropertyType.InterfaceProperty);
            case FFieldPathProperty: return DumpedPropertyType.Of(EPropertyType.FieldPathProperty);
            case FDelegateProperty: return DumpedPropertyType.Of(EPropertyType.DelegateProperty);
            case FMulticastInlineDelegateProperty:
            case FMulticastDelegateProperty:
                return DumpedPropertyType.Of(EPropertyType.MulticastDelegateProperty);
            case FVerseStringProperty: return DumpedPropertyType.Of(EPropertyType.VerseStringProperty);
            case FVerseFunctionProperty: return DumpedPropertyType.Of(EPropertyType.VerseFunctionProperty);
            case FVerseDynamicProperty: return DumpedPropertyType.Of(EPropertyType.VerseDynamicProperty);

            default:
                return DumpedPropertyType.Of(EPropertyType.Unknown);
        }
    }

    private static DumpedPropertyType ConvertInner(FProperty? inner)
        => inner != null ? ConvertProperty(inner) : DumpedPropertyType.Of(EPropertyType.Unknown);

    private static string? ResolveName(FPackageIndex? index)
    {
        if (index is null || index.IsNull) return null;
        var name = index.ResolvedObject?.Name.Text;
        return string.IsNullOrEmpty(name) || name == "None" ? null : name;
    }

    /// <summary>"EFortRarity::Common" -> "Common", matching how the dumper trims member names.</summary>
    private static string ShortEnumMember(string name)
    {
        var separator = name.IndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? name[(separator + 2)..] : name;
    }
}
