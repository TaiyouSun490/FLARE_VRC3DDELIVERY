using System;
using System.Text;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace AvatarCatalog.Remote
{
    /// <summary>Downloads a RAC2 file and reads only its portable PROD metadata for a standalone pedestal.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class Rac2ProductLoader : UdonSharpBehaviour
    {
        public const int StatusIdle = 0;
        public const int StatusLoading = 1;
        public const int StatusReady = 2;
        public const int StatusError = 3;

        [Header("Source")]
        public VRCUrl RuntimeUrl;
        public VRCUrlInputField UrlInput;

        [Header("Output")]
        public Rac2ProductController ProductController;
        public Text StatusText;

        [Header("Diagnostics")]
        public int Status;
        public string StatusMessage = "Idle";
        public string LastError = "";
        public int LastHttpErrorCode;
        public bool LoadedWasCompressed;
        public int LoadedStoredBytes;
        public string LoadedProductName = "";
        public string LoadedCreatorName = "";
        public string LoadedProductUrl = "";
        public string LoadedAvatarBlueprintId = "";
        public bool LoadedTrialEnabled;

        private const int HeaderBytes = 24;
        private const int TocEntryBytes = 16;
        private const int CompressedTocEntryBytes = 24;
        private const int ProductHeaderBytes = 24;
        private const int MaximumBytes = 67108864;
        private const uint Magic = 0x32434152u;
        private const uint CompressedContainerFlag = 1u;
        private const uint MetaType = 0x4154454du;
        private const uint MeshType = 0x4853454du;
        private const uint MatlType = 0x4c54414du;
        private const uint Tex0Type = 0x30584554u;
        private const uint TexnType = 0x4e584554u;
        private const uint VatiType = 0x49544156u;
        private const uint VatpType = 0x50544156u;
        private const uint VatnType = 0x4e544156u;
        private const uint PartType = 0x54524150u;
        private const uint PtexType = 0x58455450u;
        private const uint IntrType = 0x52544e49u;
        private const uint ProdType = 0x444f5250u;

        private byte[] _data;
        private bool _standardChecksum;
        private string _parseError = "Invalid RAC2.";

        private void Start()
        {
            bool japanese = ProductController != null && ProductController.UseJapanese;
            Text[] captions = GetComponentsInChildren<Text>(true);
            for (int i = 0; i < captions.Length; i++)
            {
                string value = captions[i].text;
                if (value == "LOAD PRODUCT") captions[i].text = japanese ? "商品情報を読み込む" : value;
                if (value == "RETRY") captions[i].text = japanese ? "再試行" : value;
                if (value == "CLEAR") captions[i].text = japanese ? "表示を消す" : value;
                if (value == "RAC2 PRODUCT PEDESTAL") captions[i].text = japanese ? "FLARE 商品ペデスタル" : "FLARE Product Pedestal";
            }
            if (Status == StatusIdle) SetStatus("Idle");
        }

        public void LoadFromInput()
        {
            if (UrlInput != null) RuntimeUrl = UrlInput.GetUrl();
            LoadRuntimeUrl();
        }

        public void LoadRuntimeUrl()
        {
            if (Status == StatusLoading) return;
            if (VRCUrl.IsNullOrEmpty(RuntimeUrl)) { ReportError("Enter a RAC2 API URL first.", 0); return; }
            ClearLoadedProduct();
            Status = StatusLoading;
            SetStatus("Downloading RAC2 product metadata...");
            VRCStringDownloader.LoadUrl(RuntimeUrl, (IUdonEventReceiver)this);
        }

        public void Retry()
        {
            LoadRuntimeUrl();
        }

        public void Clear()
        {
            ClearLoadedProduct();
            Status = StatusIdle;
            LastError = "";
            LastHttpErrorCode = 0;
            SetStatus("Idle");
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            byte[] bytes = result.ResultBytes;
            if (bytes == null || bytes.Length == 0) { ReportError("RAC2 response was empty.", 0); return; }
            if (bytes.Length > MaximumBytes) { ReportError("RAC2 exceeds the 64 MB limit.", 0); return; }
            if (!ParseAndApply(bytes)) { ReportError(_parseError, 0); return; }
            Status = StatusReady;
            LastError = "";
            LastHttpErrorCode = 0;
            SetStatus("Product ready");
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            ReportError("RAC2 download failed: " + result.Error, result.ErrorCode);
        }

        private bool ParseAndApply(byte[] bytes)
        {
            _parseError = "Invalid RAC2.";
            LoadedWasCompressed = false;
            LoadedStoredBytes = bytes.Length;
            _data = bytes;
            if (bytes.Length < HeaderBytes + TocEntryBytes * 2) return Fail("RAC2 header is truncated.");
            uint version = ReadU32At(4);
            if (ReadU32At(0) != Magic || (version != 2u && version != 3u) || ReadU32At(8) != (uint)bytes.Length)
                return Fail("RAC2 header is invalid or unsupported.");
            uint flags = ReadU32At(16);
            bool compressed = flags == 1u || flags == 3u;
            if ((!compressed && flags != 0u) || ReadU32At(20) != (compressed ? 24u : 0u))
                return Fail("RAC2 container flags are unsupported.");
            _standardChecksum = flags == 3u;
            LoadedWasCompressed = compressed;
            uint countRaw = ReadU32At(12);
            if (countRaw < (version == 3u ? 3u : 2u) || countRaw > (version == 3u ? 6u : 12u))
                return Fail("RAC2 section count is unsupported.");
            int count = (int)countRaw;
            int tocSize = compressed ? 24 : 16;
            int expectedOffset = HeaderBytes + count * tocSize;
            if (expectedOffset > bytes.Length) return Fail("RAC2 section table is truncated.");
            long rawTotal = HeaderBytes + count * 16;
            int previousRank = -1, productOffset = 0, productLength = 0, productStored = 0;
            uint productCodec = 0u, productChecksum = 0u;
            for (int section = 0; section < count; section++)
            {
                int toc = HeaderBytes + section * tocSize;
                uint type = ReadU32At(toc);
                int rank = version == 3u ? BundleSectionRank(type) : SectionRank(type);
                uint offset = ReadU32At(toc + 4), stored = ReadU32At(toc + 8);
                uint raw = compressed ? ReadU32At(toc + 12) : stored;
                uint codec = compressed ? ReadU32At(toc + 16) : 0u;
                if (rank < 0 || rank <= previousRank || offset != (uint)expectedOffset ||
                    stored == 0u || raw == 0u || raw > MaximumBytes || stored > MaximumBytes ||
                    codec > 1u || codec == 0u && stored != raw || codec == 1u && stored >= raw ||
                    !compressed && ReadU32At(toc + 12) != 0u)
                    return Fail("RAC2 section table is non-canonical.");
                if (section == 0 && type != MetaType ||
                    version == 2u && section == 1 && type != MeshType ||
                    version == 3u && section == 1 && type != 0x454e4353u ||
                    version == 3u && section == 2 && type != 0x45444f4eu)
                    return Fail("RAC2 required sections are missing.");
                long next = (long)expectedOffset + stored;
                rawTotal += raw;
                if (next > bytes.Length || rawTotal > MaximumBytes)
                    return Fail("RAC2 section exceeds the container limits.");
                if (type == ProdType)
                {
                    productOffset = expectedOffset;
                    productLength = (int)raw;
                    productStored = (int)stored;
                    productCodec = codec;
                    productChecksum = compressed ? ReadU32At(toc + 20) : 0u;
                }
                previousRank = rank;
                expectedOffset = (int)next;
            }
            if (expectedOffset != bytes.Length) return Fail("RAC2 contains a gap or trailing bytes.");
            if (productOffset != 0 && compressed)
            {
                // Metadata-only pedestals need not decompress model/VAT/texture sections.
                if (productLength > 1345) return Fail("RAC2 PROD metadata exceeds its limit.");
                byte[] product = new byte[productLength];
                if (productCodec == 0u) Buffer.BlockCopy(bytes, productOffset, product, 0, productLength);
                else if (!DecompressLz4Block(bytes, productOffset, productStored, product, 0, productLength))
                    return Fail("RAC2 PROD LZ4 is malformed.");
                if (Adler32(product, 0, productLength) != productChecksum)
                    return Fail("RAC2 PROD checksum mismatch.");
                _data = product;
                productOffset = 0;
                // The section has been found; zero is a valid offset in this extracted payload.
                return ParseProductAt(0, productLength);
            }
            if (productOffset == 0) return Fail("This RAC2 file has no PROD metadata.");
            return ParseProductAt(productOffset, productLength);
        }

        private int BundleSectionRank(uint type)
        {
            if (type == MetaType) return 0;
            if (type == 0x454e4353u) return 1;
            if (type == 0x45444f4eu) return 2;
            if (type == PartType) return 3;
            if (type == IntrType) return 4;
            if (type == ProdType) return 5;
            return -1;
        }

        private bool ParseProductAt(int productOffset, int productLength)
        {
            if (productLength < ProductHeaderBytes || ReadU32At(productOffset) != 1u)
                return Fail("RAC2 PROD format is unsupported.");

            uint flags = ReadU32At(productOffset + 4);
            uint nameLengthRaw = ReadU32At(productOffset + 8);
            uint creatorLengthRaw = ReadU32At(productOffset + 12);
            uint urlLengthRaw = ReadU32At(productOffset + 16);
            uint avatarLengthRaw = ReadU32At(productOffset + 20);
            long payloadLength = (long)nameLengthRaw + creatorLengthRaw + urlLengthRaw + avatarLengthRaw;
            if ((flags & ~1u) != 0u || nameLengthRaw > 128u || creatorLengthRaw > 128u ||
                urlLengthRaw > 1024u || avatarLengthRaw > 41u || payloadLength == 0L ||
                payloadLength != productLength - ProductHeaderBytes)
                return Fail("RAC2 PROD metadata is invalid.");

            int cursor = productOffset + ProductHeaderBytes;
            string productName = Encoding.UTF8.GetString(_data, cursor, (int)nameLengthRaw);
            cursor += (int)nameLengthRaw;
            string creatorName = Encoding.UTF8.GetString(_data, cursor, (int)creatorLengthRaw);
            cursor += (int)creatorLengthRaw;
            string productUrl = Encoding.UTF8.GetString(_data, cursor, (int)urlLengthRaw);
            cursor += (int)urlLengthRaw;
            string avatarId = Encoding.UTF8.GetString(_data, cursor, (int)avatarLengthRaw);
            bool trial = (flags & 1u) != 0u;
            bool validUrl = string.IsNullOrEmpty(productUrl) || productUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            bool validAvatar = string.IsNullOrEmpty(avatarId) || avatarId.Length == 41 && avatarId.StartsWith("avtr_");
            if (!validUrl || !validAvatar || trial && avatarId.Length != 41)
                return Fail("RAC2 PROD URL or Avatar Blueprint ID is invalid.");

            LoadedProductName = productName;
            LoadedCreatorName = creatorName;
            LoadedProductUrl = productUrl;
            LoadedAvatarBlueprintId = avatarId;
            LoadedTrialEnabled = trial;
            if (ProductController == null) return Fail("RAC2 product controller is missing.");
            ProductController.ApplyProduct(productName, creatorName, productUrl, avatarId, trial);
            _data = null;
            return true;
        }

        private byte[] ExpandCompressedContainer(byte[] source)
        {
            if (source.Length < HeaderBytes + CompressedTocEntryBytes * 2 || ReadU32At(20) != (uint)CompressedTocEntryBytes)
            {
                Fail("RAC2 compressed directory is truncated or unsupported.");
                return null;
            }
            uint countRaw = ReadU32At(12);
            if (countRaw < 2u || countRaw > 12u)
            {
                Fail("RAC2 compressed section count is unsupported.");
                return null;
            }
            int count = (int)countRaw;
            int storedOffset = HeaderBytes + count * CompressedTocEntryBytes;
            long rawTotal = HeaderBytes + count * TocEntryBytes;
            for (int section = 0; section < count; section++)
            {
                int toc = HeaderBytes + section * CompressedTocEntryBytes;
                uint offsetRaw = ReadU32At(toc + 4);
                uint storedLengthRaw = ReadU32At(toc + 8);
                uint rawLengthRaw = ReadU32At(toc + 12);
                uint codec = ReadU32At(toc + 16);
                if (offsetRaw != (uint)storedOffset || storedLengthRaw == 0u || rawLengthRaw == 0u ||
                    storedLengthRaw > int.MaxValue || rawLengthRaw > MaximumBytes || codec > 1u ||
                    codec == 0u && storedLengthRaw != rawLengthRaw || codec == 1u && storedLengthRaw >= rawLengthRaw)
                {
                    Fail("RAC2 compressed section table is non-canonical.");
                    return null;
                }
                long nextStored = (long)storedOffset + storedLengthRaw;
                rawTotal += rawLengthRaw;
                if (nextStored > source.Length || rawTotal > MaximumBytes)
                {
                    Fail("RAC2 compressed section exceeds the memory limit.");
                    return null;
                }
                storedOffset = (int)nextStored;
            }
            if (storedOffset != source.Length)
            {
                Fail("RAC2 compressed container has gaps or trailing bytes.");
                return null;
            }

            byte[] expanded = new byte[(int)rawTotal];
            WriteU32At(expanded, 0, Magic);
            WriteU32At(expanded, 4, 2u);
            WriteU32At(expanded, 8, (uint)expanded.Length);
            WriteU32At(expanded, 12, countRaw);
            WriteU32At(expanded, 16, 0u);
            WriteU32At(expanded, 20, 0u);
            int rawOffset = HeaderBytes + count * TocEntryBytes;
            for (int section = 0; section < count; section++)
            {
                int toc = HeaderBytes + section * CompressedTocEntryBytes;
                uint type = ReadU32At(toc);
                int sectionStoredOffset = (int)ReadU32At(toc + 4);
                int storedLength = (int)ReadU32At(toc + 8);
                int rawLength = (int)ReadU32At(toc + 12);
                uint codec = ReadU32At(toc + 16);
                uint checksum = ReadU32At(toc + 20);
                int legacyToc = HeaderBytes + section * TocEntryBytes;
                WriteU32At(expanded, legacyToc, type);
                WriteU32At(expanded, legacyToc + 4, (uint)rawOffset);
                WriteU32At(expanded, legacyToc + 8, (uint)rawLength);
                WriteU32At(expanded, legacyToc + 12, 0u);
                if (codec == 0u) Buffer.BlockCopy(source, sectionStoredOffset, expanded, rawOffset, rawLength);
                else if (!DecompressLz4Block(source, sectionStoredOffset, storedLength, expanded, rawOffset, rawLength))
                {
                    Fail("RAC2 LZ4 section is malformed.");
                    return null;
                }
                if (Adler32(expanded, rawOffset, rawLength) != checksum)
                {
                    Fail("RAC2 section checksum mismatch.");
                    return null;
                }
                rawOffset += rawLength;
            }
            return rawOffset == expanded.Length ? expanded : null;
        }

        private bool DecompressLz4Block(byte[] source, int sourceOffset, int sourceLength, byte[] target, int targetOffset, int targetLength)
        {
            int input = sourceOffset;
            int inputEnd = sourceOffset + sourceLength;
            int output = targetOffset;
            int outputEnd = targetOffset + targetLength;
            while (input < inputEnd)
            {
                int token = source[input++];
                int literalLength = token >> 4;
                if (literalLength == 15)
                {
                    int extension;
                    do { if (input >= inputEnd) return false; extension = source[input++]; literalLength += extension; }
                    while (extension == 255);
                }
                if (literalLength > inputEnd - input || literalLength > outputEnd - output) return false;
                Buffer.BlockCopy(source, input, target, output, literalLength);
                input += literalLength;
                output += literalLength;
                if (input == inputEnd) return output == outputEnd;
                if (input + 2 > inputEnd) return false;
                int distance = source[input] | source[input + 1] << 8;
                input += 2;
                if (distance == 0 || distance > output - targetOffset) return false;
                int matchLength = token & 15;
                if (matchLength == 15)
                {
                    int extension;
                    do { if (input >= inputEnd) return false; extension = source[input++]; matchLength += extension; }
                    while (extension == 255);
                }
                matchLength += 4;
                if (matchLength > outputEnd - output) return false;
                int reference = output - distance;
                int remaining = matchLength;
                while (remaining > 0)
                {
                    // Copy only bytes that already exist, then grow the repeated
                    // region exponentially. This preserves LZ4 overlap semantics
                    // without executing one Udon VM loop per output byte.
                    int available = output - reference;
                    int copyLength = remaining < available ? remaining : available;
                    Buffer.BlockCopy(target, reference, target, output, copyLength);
                    output += copyLength;
                    remaining -= copyLength;
                }
            }
            return output == outputEnd;
        }

        private uint Adler32(byte[] data, int offset, int length)
        {
            const int modulus = 65521;
            int a = 1;
            int b = 0;
            int end = offset + length;
            while (offset < end)
            {
                int blockEnd = offset + (_standardChecksum ? 2776 : 5552);
                if (blockEnd > end) blockEnd = end;
                while (offset < blockEnd) { a += data[offset++]; b += a; }
                a %= modulus;
                b %= modulus;
            }
            return ((uint)b << 16) | (uint)a;
        }

        private int SectionRank(uint type)
        {
            if (type == MetaType) return 0;
            if (type == MeshType) return 1;
            if (type == MatlType) return 2;
            if (type == Tex0Type) return 3;
            if (type == TexnType) return 4;
            if (type == VatiType) return 5;
            if (type == VatpType) return 6;
            if (type == VatnType) return 7;
            if (type == PartType) return 8;
            if (type == PtexType) return 9;
            if (type == IntrType) return 10;
            if (type == ProdType) return 11;
            return -1;
        }

        private uint ReadU32At(int offset)
        {
            return (uint)_data[offset] | (uint)_data[offset + 1] << 8 |
                   (uint)_data[offset + 2] << 16 | (uint)_data[offset + 3] << 24;
        }

        private void WriteU32At(byte[] data, int offset, uint value)
        {
            // Keep every operand within byte range before Udon's checked cast.
            data[offset] = (byte)(value & 0xFFu);
            data[offset + 1] = (byte)((value >> 8) & 0xFFu);
            data[offset + 2] = (byte)((value >> 16) & 0xFFu);
            data[offset + 3] = (byte)((value >> 24) & 0xFFu);
        }

        private bool Fail(string message)
        {
            _parseError = message;
            _data = null;
            return false;
        }

        private void ClearLoadedProduct()
        {
            LoadedWasCompressed = false;
            LoadedStoredBytes = 0;
            LoadedProductName = "";
            LoadedCreatorName = "";
            LoadedProductUrl = "";
            LoadedAvatarBlueprintId = "";
            LoadedTrialEnabled = false;
            if (ProductController != null) ProductController.ClearProduct();
        }

        private void ReportError(string message, int httpCode)
        {
            Status = StatusError;
            LastError = message;
            LastHttpErrorCode = httpCode;
            SetStatus("Error: " + message);
            Debug.LogWarning("[RAC2 Product Loader] " + message);
        }

        private void SetStatus(string message)
        {
            StatusMessage = message;
            if (StatusText == null) return;
            bool japanese = ProductController != null && ProductController.UseJapanese;
            if (!japanese) { StatusText.text = message; return; }
            if (message == "Idle") StatusText.text = "公開HTTPSの.rac2 URLを入力してください。";
            else if (message == "Product ready") StatusText.text = "商品情報を読み込みました。";
            else if (message == "Downloading RAC2 product metadata...") StatusText.text = "RAC2の商品情報をダウンロード中…";
            else if (Status == StatusError)
            {
                if (LastError == "Enter a RAC2 API URL first.") StatusText.text = "RAC2のHTTPS URLを入力してください。";
                else if (LastError == "RAC2 exceeds the 64 MB limit.") StatusText.text = "ペデスタル単体版の上限64 MiBを超えています。ファイルを小さくしてください。";
                else if (LastHttpErrorCode != 0) StatusText.text = "ダウンロードに失敗しました。URL・公開設定・Allow Untrusted URLsを確認してください。HTTP: " + LastHttpErrorCode;
                else StatusText.text = "商品情報を読み込めません。Creatorでカタログ情報を含めて再作成してください。技術情報はConsoleを確認してください。";
            }
            else StatusText.text = "RAC2の商品情報を処理中…";
        }
    }
}
