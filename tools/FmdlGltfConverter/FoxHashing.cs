using System.Collections.ObjectModel;

namespace FmdlGltfConverter;

internal static class FoxHashing
{
    public const ulong MetaFlag = 0x4000000000000;
    private const ulong PathCodeMask = 0x1568000000000000;

    private static readonly ReadOnlyCollection<string> FileExtensions = new(new[]
    {
        "1.ftexs", "1.nav2", "2.ftexs", "3.ftexs", "4.ftexs", "5.ftexs", "6.ftexs",
        "ag.evf", "aia", "aib", "aibc", "aig", "aigc", "aim", "aip", "ait", "atsh",
        "bnd", "bnk", "cc.evf", "clo", "csnav", "dat", "des", "dnav", "dnav2",
        "eng.lng", "ese", "evb", "evf", "fag", "fage", "fago", "fagp", "fagx",
        "fclo", "fcnp", "fcnpx", "fdes", "fdmg", "ffnt", "fmdl", "fmdlb", "fmtt",
        "fnt", "fova", "fox", "fox2", "fpk", "fpkd", "fpkl", "frdv", "fre.lng",
        "frig", "frt", "fsd", "fsm", "fsml", "fsop", "fstb", "ftex", "fv2",
        "fx.evf", "fxp", "gani", "geom", "ger.lng", "gpfp", "grxla", "grxoc",
        "gskl", "htre", "info", "ita.lng", "jpn.lng", "json", "lad", "ladb", "lani",
        "las", "lba", "lng", "lpsh", "lua", "mas", "mbl", "mog", "mtar", "mtl",
        "nav2", "nta", "obr", "obrb", "parts", "path", "pftxs", "ph", "phep",
        "phsd", "por.lng", "qar", "rbs", "rdb", "rdf", "rnav", "rus.lng", "sad",
        "sand", "sani", "sbp", "sd.evf", "sdf", "sim", "simep", "snav", "spa.lng",
        "spch", "sub", "subp", "tgt", "tre2", "txt", "uia", "uif", "uig", "uigb",
        "uil", "uilb", "utxl", "veh", "vfx", "vfxbin", "vfxdb", "vnav", "vo.evf",
        "vpc", "wem", "xml",
    });

    private static readonly IReadOnlyDictionary<ulong, string> ExtensionMap = FileExtensions.ToDictionary(HashFileExtension);

    public static ulong HashFileExtension(string fileExtension)
    {
        return HashFileName(fileExtension, removeExtension: false) & 0x1FFF;
    }

    public static ulong HashFileNameLegacy(string text, bool removeExtension = true)
    {
        if (removeExtension)
        {
            int index = text.IndexOf('.');
            text = index == -1 ? text : text[..index];
        }

        const ulong seed0 = 0x9ae16a3b2f90404f;
        ulong seed1 = text.Length > 0 ? ((uint)text[0] << 16) + (uint)text.Length : 0;
        return CityHash.CityHash.CityHash64WithSeeds(text + "\0", seed0, seed1) & 0xFFFFFFFFFFFF;
    }

    public static ulong HashFileName(string text, bool removeExtension = true)
    {
        if (removeExtension)
        {
            int index = text.IndexOf('.');
            text = index == -1 ? text : text[..index];
        }

        bool metaFlag;
        const string assetsPrefix = "/Assets/";

        if (text.StartsWith(assetsPrefix, StringComparison.Ordinal))
        {
            text = text[assetsPrefix.Length..];
            metaFlag = text.StartsWith("tpptest", StringComparison.Ordinal);
        }
        else
        {
            metaFlag = true;
        }

        text = text.TrimStart('/');

        const ulong seed0 = 0x9ae16a3b2f90404f;
        Span<byte> seed1Bytes = stackalloc byte[sizeof(ulong)];

        for (int textIndex = text.Length - 1, seedIndex = 0; textIndex >= 0 && seedIndex < seed1Bytes.Length; textIndex--, seedIndex++)
        {
            seed1Bytes[seedIndex] = Convert.ToByte(text[textIndex]);
        }

        ulong seed1 = BitConverter.ToUInt64(seed1Bytes);
        ulong maskedHash = CityHash.CityHash.CityHash64WithSeeds(text, seed0, seed1) & 0x3FFFFFFFFFFFF;

        return metaFlag ? maskedHash | MetaFlag : maskedHash;
    }

    public static ulong HashFileNameWithExtension(string filePath)
    {
        filePath = DenormalizeFilePath(filePath);

        int extensionIndex = filePath.IndexOf('.', StringComparison.Ordinal);
        string hashablePart;
        string extensionPart;

        if (extensionIndex == -1)
        {
            hashablePart = filePath;
            extensionPart = string.Empty;
        }
        else
        {
            hashablePart = filePath[..extensionIndex];
            extensionPart = filePath[(extensionIndex + 1)..];
        }

        ulong typeId = 0;
        KeyValuePair<ulong, string>? matchedExtension = ExtensionMap.FirstOrDefault(pair => pair.Value == extensionPart);
        if (matchedExtension.HasValue)
        {
            typeId = matchedExtension.Value.Key;
        }

        ulong hash = HashFileName(hashablePart);
        return (typeId << 51) | hash;
    }

    public static string DenormalizeFilePath(string filePath)
    {
        return filePath.Replace("\\", "/", StringComparison.Ordinal);
    }

    public static ulong StripPathCodePrefix(ulong hash)
    {
        return hash - PathCodeMask;
    }
}
