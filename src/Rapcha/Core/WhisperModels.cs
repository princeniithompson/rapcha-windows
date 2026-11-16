using System.Collections.Generic;
using System.Linq;

namespace Rapcha.Core
{
    /// <summary>One whisper.cpp model file from the official ggml models on Hugging Face (ggerganov/whisper.cpp).</summary>
    public sealed class WhisperModel
    {
        public string Id;        // "tiny.en"
        public string File;      // "ggml-tiny.en.bin"
        public long Bytes;
        public string Sha256;    // from the Hugging Face file listing (Git LFS object id)
        public string Label;     // "Tiny (English), 75 MB"
        public long RamMb;       // memory it needs while talking (model + working buffers, measured roughly)

        public string Url => WhisperModels.BaseUrl + File;
        public long Mb => (Bytes + 512 * 1024) / (1024 * 1024);
    }

    public static class WhisperModels
    {
        public const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

        public static readonly WhisperModel Tiny = new WhisperModel
        {
            Id = "tiny.en", File = "ggml-tiny.en.bin", Bytes = 77704715,
            Sha256 = "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f", Label = "Tiny (English), 74 MB", RamMb = 300,
        };
        public static readonly WhisperModel Base = new WhisperModel
        {
            Id = "base.en", File = "ggml-base.en.bin", Bytes = 147964211,
            Sha256 = "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002", Label = "Base (English), 141 MB", RamMb = 450,
        };
        public static readonly WhisperModel Small = new WhisperModel
        {
            Id = "small.en", File = "ggml-small.en.bin", Bytes = 487614201,
            Sha256 = "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d", Label = "Small (English), 465 MB, best but slower", RamMb = 1000,
        };

        public static readonly WhisperModel[] All = { Tiny, Base, Small };

        public static WhisperModel ById(string id) => All.FirstOrDefault(m => m.Id == id);

        /// <summary>Models this tier may use, recommended first. Low: none (built-in Windows speech instead).</summary>
        public static IList<WhisperModel> ForTier(DeviceTier tier)
        {
            switch (tier)
            {
                case DeviceTier.Medium: return new[] { Tiny };
                case DeviceTier.Strong: return new[] { Base, Small };
                default: return new WhisperModel[0];
            }
        }

        public static WhisperModel Recommended(DeviceTier tier) => ForTier(tier).FirstOrDefault();

        /// <summary>The model to use: the chosen one when this tier allows it, else the tier's recommendation.</summary>
        public static WhisperModel Choose(DeviceTier tier, string chosenId)
        {
            var allowed = ForTier(tier);
            return allowed.FirstOrDefault(m => m.Id == chosenId) ?? allowed.FirstOrDefault();
        }

        /// <summary>Enough free memory right now to load the model and keep the laptop responsive?</summary>
        public static bool FitsInFreeMemory(WhisperModel m, long freeRamMb) => m != null && freeRamMb >= m.RamMb + SessionCaps.MinFreeMbWhisper;

        /// <summary>Enough disk for the download (and a little room left over)?</summary>
        public static bool FitsOnDisk(WhisperModel m, long freeDiskMb) => m != null && freeDiskMb >= m.Mb + 500;
    }
}
