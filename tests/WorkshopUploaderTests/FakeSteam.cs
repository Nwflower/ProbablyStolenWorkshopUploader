using System;
using System.Collections.Generic;

// 离线替身：记录生产代码调用，并由测试显式触发 Steam 回调，不连接客户端或工坊。
namespace Steamworks
{
    internal static class Fake
    {
        public static bool Online = true;
        public static bool FailDescription;
        public static bool FailTags;
        public static bool InvalidSubmitCall;
        public static EItemUpdateStatus ProgressStatus;
        public static ulong ProgressDone;
        public static ulong ProgressTotal;
        public static readonly List<string> Calls = new List<string>();
        public static readonly List<string> SubmittedNotes = new List<string>();
        public static readonly List<uint> PagesRequested = new List<uint>();
        public static readonly Dictionary<ulong, List<SteamUGCDetails_t>> Pages = new Dictionary<ulong, List<SteamUGCDetails_t>>();
        public static readonly Dictionary<(ulong Handle, uint Index), List<string>> QueryTags = new Dictionary<(ulong, uint), List<string>>();
        public static Action<object, bool> Pending;

        public static void Reset()
        {
            Online = true;
            FailDescription = false;
            FailTags = false;
            InvalidSubmitCall = false;
            ProgressStatus = EItemUpdateStatus.k_EItemUpdateStatusInvalid;
            ProgressDone = ProgressTotal = 0;
            Calls.Clear();
            SubmittedNotes.Clear();
            PagesRequested.Clear();
            Pages.Clear();
            QueryTags.Clear();
            Pending = null;
        }

        public static void Complete<T>(T result, bool ioFailure = false)
        {
            Action<object, bool> callback = Pending ?? throw new Exception("没有等待中的 Steam 回调");
            Pending = null;
            callback(result, ioFailure);
        }
    }

    public sealed class CallResult<T>
    {
        private readonly Action<T, bool> _callback;
        private CallResult(Action<T, bool> callback) { _callback = callback; }
        public static CallResult<T> Create(Action<T, bool> callback) => new CallResult<T>(callback);
        public void Set(SteamAPICall_t call) { Fake.Pending = (value, io) => _callback((T)value, io); }
        public void Cancel() { Fake.Pending = null; }
    }

    public struct SteamAPICall_t : IEquatable<SteamAPICall_t>
    {
        public ulong Value;
        public SteamAPICall_t(ulong value) { Value = value; }
        public static SteamAPICall_t Invalid => new SteamAPICall_t(0);
        public static bool operator ==(SteamAPICall_t a, SteamAPICall_t b) => a.Value == b.Value;
        public static bool operator !=(SteamAPICall_t a, SteamAPICall_t b) => !(a == b);
        public bool Equals(SteamAPICall_t other) => this == other;
        public override bool Equals(object obj) => obj is SteamAPICall_t other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
    }
    public struct AppId_t
    {
        public uint m_AppId;
        public AppId_t(uint id) { m_AppId = id; }
    }
    public struct AccountID_t { public AccountID_t(uint id) { } }
    public struct CSteamID { public ulong m_SteamID; }
    public struct PublishedFileId_t
    {
        public ulong m_PublishedFileId;
        public PublishedFileId_t(ulong id) { m_PublishedFileId = id; }
        public static PublishedFileId_t Invalid => new PublishedFileId_t(0);
    }
    public struct UGCQueryHandle_t : IEquatable<UGCQueryHandle_t>
    {
        public ulong Value;
        public UGCQueryHandle_t(ulong value) { Value = value; }
        public static UGCQueryHandle_t Invalid => new UGCQueryHandle_t(0);
        public static bool operator ==(UGCQueryHandle_t a, UGCQueryHandle_t b) => a.Value == b.Value;
        public static bool operator !=(UGCQueryHandle_t a, UGCQueryHandle_t b) => !(a == b);
        public bool Equals(UGCQueryHandle_t other) => this == other;
        public override bool Equals(object obj) => obj is UGCQueryHandle_t other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
    }
    public struct UGCUpdateHandle_t : IEquatable<UGCUpdateHandle_t>
    {
        public ulong Value;
        public UGCUpdateHandle_t(ulong value) { Value = value; }
        public static UGCUpdateHandle_t Invalid => new UGCUpdateHandle_t(0);
        public static bool operator ==(UGCUpdateHandle_t a, UGCUpdateHandle_t b) => a.Value == b.Value;
        public static bool operator !=(UGCUpdateHandle_t a, UGCUpdateHandle_t b) => !(a == b);
        public bool Equals(UGCUpdateHandle_t other) => this == other;
        public override bool Equals(object obj) => obj is UGCUpdateHandle_t other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
    }

    public enum EResult { k_EResultOK, k_EResultNoConnection, k_EResultFail }
    public enum ERemoteStoragePublishedFileVisibility
    {
        k_ERemoteStoragePublishedFileVisibilityPublic,
        k_ERemoteStoragePublishedFileVisibilityFriendsOnly,
        k_ERemoteStoragePublishedFileVisibilityPrivate,
        k_ERemoteStoragePublishedFileVisibilityUnlisted
    }
    public enum EWorkshopFileType { k_EWorkshopFileTypeFirst }
    public enum EUserUGCList { k_EUserUGCList_Published }
    public enum EUGCMatchingUGCType { k_EUGCMatchingUGCType_Items }
    public enum EUserUGCListSortOrder { k_EUserUGCListSortOrder_CreationOrderDesc }
    public enum EItemUpdateStatus
    {
        k_EItemUpdateStatusInvalid, k_EItemUpdateStatusPreparingConfig, k_EItemUpdateStatusPreparingContent,
        k_EItemUpdateStatusUploadingContent, k_EItemUpdateStatusUploadingPreviewFile, k_EItemUpdateStatusCommittingChanges
    }
    public struct CreateItemResult_t
    {
        public EResult m_eResult;
        public bool m_bUserNeedsToAcceptWorkshopLegalAgreement;
        public PublishedFileId_t m_nPublishedFileId;
    }
    public struct SubmitItemUpdateResult_t
    {
        public EResult m_eResult;
        public bool m_bUserNeedsToAcceptWorkshopLegalAgreement;
    }
    public struct SteamUGCQueryCompleted_t
    {
        public UGCQueryHandle_t m_handle;
        public EResult m_eResult;
        public uint m_unNumResultsReturned;
        public uint m_unTotalMatchingResults;
    }
    public struct SteamUGCDetails_t
    {
        public PublishedFileId_t m_nPublishedFileId;
        public string m_rgchTitle;
        public ERemoteStoragePublishedFileVisibility m_eVisibility;
    }

    public static class SteamAPI
    {
        public static bool Init() => true;
        public static void RunCallbacks() { }
        public static void Shutdown() { }
    }
    public static class SteamUser
    {
        public static bool BLoggedOn() => Fake.Online;
        public static CSteamID GetSteamID() => new CSteamID { m_SteamID = 123 };
    }
    public static class SteamFriends { public static string GetPersonaName() => "test"; }
    public static class SteamUtils { public static AppId_t GetAppID() => new AppId_t(4348910); }

    public static class SteamUGC
    {
        private static bool Record(string name) { Fake.Calls.Add(name); return true; }
        public static SteamAPICall_t CreateItem(AppId_t app, EWorkshopFileType type)
        {
            Record("create");
            return new SteamAPICall_t(1);
        }
        public static UGCUpdateHandle_t StartItemUpdate(AppId_t app, PublishedFileId_t id)
        {
            Record("update:" + id.m_PublishedFileId);
            return new UGCUpdateHandle_t(1);
        }
        public static bool SetItemUpdateLanguage(UGCUpdateHandle_t h, string lang) => Record("language:" + lang);
        public static bool SetItemTitle(UGCUpdateHandle_t h, string title) => Record("title:" + title);
        public static bool SetItemDescription(UGCUpdateHandle_t h, string desc)
        {
            Record("description:" + desc);
            return !Fake.FailDescription;
        }
        public static bool SetItemContent(UGCUpdateHandle_t h, string folder) => Record("content");
        public static bool SetItemPreview(UGCUpdateHandle_t h, string file) => Record("preview");
        public static bool SetItemVisibility(UGCUpdateHandle_t h, ERemoteStoragePublishedFileVisibility visibility) => Record("visibility:" + (int)visibility);
        public static bool AddItemKeyValueTag(UGCUpdateHandle_t h, string key, string value) => Record("tag:" + key);
        public static bool SetItemTags(UGCUpdateHandle_t h, IList<string> pTags)
        {
            Record("tags:" + string.Join("|", pTags ?? new List<string>()));
            return !Fake.FailTags;
        }
        public static SteamAPICall_t SubmitItemUpdate(UGCUpdateHandle_t h, string note)
        {
            Record("submit");
            Fake.SubmittedNotes.Add(note);
            return Fake.InvalidSubmitCall ? SteamAPICall_t.Invalid : new SteamAPICall_t(1);
        }
        public static EItemUpdateStatus GetItemUpdateProgress(UGCUpdateHandle_t h, out ulong done, out ulong total)
        {
            done = Fake.ProgressDone;
            total = Fake.ProgressTotal;
            return Fake.ProgressStatus;
        }
        public static UGCQueryHandle_t CreateQueryUserUGCRequest(
            AccountID_t account, EUserUGCList list, EUGCMatchingUGCType type,
            EUserUGCListSortOrder sort, AppId_t creator, AppId_t consumer, uint page)
        {
            Fake.PagesRequested.Add(page);
            return new UGCQueryHandle_t(page);
        }
        public static bool SetReturnKeyValueTags(UGCQueryHandle_t h, bool value) => true;
        public static SteamAPICall_t SendQueryUGCRequest(UGCQueryHandle_t h) => new SteamAPICall_t(1);
        public static bool GetQueryUGCResult(UGCQueryHandle_t h, uint index, out SteamUGCDetails_t details)
        {
            details = Fake.Pages[h.Value][(int)index];
            return true;
        }
        public static bool GetQueryUGCKeyValueTag(UGCQueryHandle_t h, uint index, string key, out string value, uint size)
        {
            value = "";
            return false;
        }
        public static uint GetQueryUGCNumTags(UGCQueryHandle_t h, uint index)
        {
            return Fake.QueryTags.TryGetValue((h.Value, index), out List<string> tags) ? (uint)tags.Count : 0;
        }
        public static bool GetQueryUGCTag(UGCQueryHandle_t h, uint index, uint indexTag, out string value, uint size)
        {
            value = "";
            if (Fake.QueryTags.TryGetValue((h.Value, index), out List<string> tags) && indexTag < tags.Count)
            {
                value = tags[(int)indexTag];
                return true;
            }
            return false;
        }
        public static bool ReleaseQueryUGCRequest(UGCQueryHandle_t h) => true;
    }
}
