using System;
using System.Diagnostics;
using System.Threading;
using eft_dma_radar.Common.DMA;
using eft_dma_radar.Common.Misc;
using eft_dma_radar.Common.Unity;
using SDK;

namespace eft_dma_radar.Tarkov.Unity.IL2CPP
{
    /// <summary>
    /// Resolves the live <c>MatchingProgress</c> instance.
    ///
    /// Strategy: <c>GameObjectManager.FindBehaviourByClassName("MatchingProgressView")</c>
    /// returns the <c>objectClass</c> ptr of the first matching component, identical to the
    /// pattern used by <c>AntiAfk</c> for <c>TarkovApplication</c>.
    /// From there: <c>objectClass + Offsets.MatchingProgressView._matchingProgress</c>
    /// → <c>MatchingProgress</c> instance pointer.
    ///
    /// <c>MatchingProgressView</c> is a pre-raid matchmaking UI MonoBehaviour — it only
    /// exists in the GOM while the queue / matching screen is active.
    /// </summary>
    internal static class MatchingProgressResolver
    {
        private const string Tag = "[MatchingProgressResolver]";

        private static ulong _cachedMatchingProgress;
        private static ulong _cachedViewObjectClass;
        private static Enums.EMatchingStage _cachedStage;
        private static readonly object _lock = new();
        private static volatile bool _resolvingAsync;

        // ─────────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clear cached pointer (call on raid start / raid stop).
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _cachedMatchingProgress = 0;
                _cachedViewObjectClass  = 0;
                _cachedStage            = Enums.EMatchingStage.None;
            }
            _resolvingAsync = false;
            XMLogging.WriteLine($"{Tag} Cache invalidated via Reset().");
        }

        /// <summary>
        /// Non-blocking cache read.
        /// Returns <c>true</c> (and a non-zero <paramref name="matchingProgress"/>) if a
        /// valid pointer is cached.
        /// </summary>
        public static bool TryGetCached(out ulong matchingProgress)
        {
            lock (_lock)
            {
                matchingProgress = _cachedMatchingProgress;
                return matchingProgress.IsValidVirtualAddress();
            }
        }

        /// <summary>
        /// Fire-and-forget background resolve.
        /// Safe to call from any thread; does not block caller.
        /// </summary>
        public static void ResolveAsync()
        {
            if (_resolvingAsync)
                return;

            _resolvingAsync = true;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var mp = GetMatchingProgress();
                    if (mp.IsValidVirtualAddress())
                        XMLogging.WriteLine($"{Tag} Resolved MatchingProgress @ 0x{mp:X}");
                    else
                        Debug.WriteLine($"{Tag} MatchingProgressView not found in GOM (not active yet).");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{Tag} ResolveAsync error: {ex}");
                }
                finally
                {
                    _resolvingAsync = false;
                }
            });
        }

        /// <summary>
        /// Synchronous resolver. Returns the cached value on subsequent calls.
        /// Walks the GOM by class name — same pattern as <c>AntiAfk.TarkovApplication</c>.
        /// </summary>
        public static ulong GetMatchingProgress()
        {
            if (TryGetCached(out var cached))
                return cached;

            try
            {
                var unityBase = Memory.UnityBase;
                if (unityBase == 0)
                    return 0;

                var gomAddr = GameObjectManager.GetAddr(unityBase);
                var gom     = GameObjectManager.Get(gomAddr);

                // FindBehaviourByClassName returns the objectClass ptr of the first
                // component whose IL2CPP class name matches — exactly like AntiAfk does
                // for "TarkovApplication".
                var viewObjectClass = gom.FindBehaviourByClassName("MatchingProgressView");
                if (!viewObjectClass.IsValidVirtualAddress())
                {
                    Debug.WriteLine($"{Tag} MatchingProgressView not in GOM active list.");
                    return 0;
                }

                XMLogging.WriteLine($"{Tag} MatchingProgressView objectClass @ 0x{viewObjectClass:X}");

                var mpPtr = Memory.ReadPtr(viewObjectClass + Offsets.MatchingProgressView._matchingProgress);
                if (!mpPtr.IsValidVirtualAddress())
                {
                    XMLogging.WriteLine($"{Tag} _matchingProgress ptr invalid @ objectClass+0x{Offsets.MatchingProgressView._matchingProgress:X}");
                    return 0;
                }

                lock (_lock)
                {
                    _cachedViewObjectClass  = viewObjectClass;
                    _cachedMatchingProgress = mpPtr;
                }

                XMLogging.WriteLine($"{Tag} MatchingProgress resolved and cached @ 0x{mpPtr:X}");
                // LogViewSnapshot(viewObjectClass); // one-shot diagnostics — uncomment when needed
                // LogSnapshot(mpPtr);               // one-shot diagnostics — uncomment when needed
                TryUpdateStage();
                return mpPtr;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{Tag} GetMatchingProgress error: {ex}");
                return 0;
            }
        }

        /// <summary>
        /// Reads the live <c>CurrentStage</c> from the cached <c>MatchingProgress</c> pointer,
        /// updates <see cref="_cachedStage"/>, and logs it. Call on every pre-raid loop tick.
        /// Returns <c>true</c> when the pointer is valid and the read succeeded.
        /// </summary>
        public static bool TryUpdateStage()
        {
            ulong mp;
            lock (_lock)
                mp = _cachedMatchingProgress;

            if (!mp.IsValidVirtualAddress())
                return false;

            try
            {
                var stage = (Enums.EMatchingStage)Memory.ReadValue<int>(mp + Offsets.MatchingProgress.CurrentStage, useCache: false);

                lock (_lock)
                    _cachedStage = stage;

                XMLogging.WriteLine($"{Tag} Stage={stage}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{Tag} TryUpdateStage error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns the last successfully read <c>CurrentStage</c> without a memory read.
        /// Safe to call from the render thread.
        /// </summary>
        public static Enums.EMatchingStage GetCachedStage()
        {
            lock (_lock)
                return _cachedStage;
        }

        /// <summary>
        /// Reads the <c>MatchingProgressView</c> component-level fields and writes them to
        /// <c>XMLogging</c>. Uses the cached <c>viewObjectClass</c> when <paramref name="view"/> is 0.
        /// </summary>
        public static void LogViewSnapshot(ulong view = 0)
        {
            if (view == 0)
            {
                lock (_lock)
                    view = _cachedViewObjectClass;
            }

            if (!view.IsValidVirtualAddress())
            {
                XMLogging.WriteLine($"{Tag} LogViewSnapshot — no valid MatchingProgressView objectClass pointer.");
                return;
            }

            try
            {
                var serversLimited         = Memory.ReadValue<bool>(view + Offsets.MatchingProgressView._serversLimited,           useCache: false);
                var canUpdateStatus        = Memory.ReadValue<bool>(view + Offsets.MatchingProgressView._canUpdateStatus,          useCache: false);
                var maxMatchingTime        = Memory.ReadValue<int> (view + Offsets.MatchingProgressView._maxMatchingTimeInSeconds, useCache: false);
                var warningHasValue        = Memory.ReadValue<bool>(view + Offsets.MatchingProgressView._matchingWarningType_hasValue, useCache: false);
                var warningRaw             = warningHasValue
                    ? Memory.ReadValue<int>(view + Offsets.MatchingProgressView._matchingWarningType, useCache: false)
                    : (int?)null;

                XMLogging.WriteLine(
                    $"{Tag} ViewSnapshot @ 0x{view:X} | " +
                    $"ServersLimited={serversLimited} CanUpdateStatus={canUpdateStatus} " +
                    $"MaxMatchingTime={maxMatchingTime}s " +
                    $"MatchingWarning={(warningRaw.HasValue ? warningRaw.Value.ToString() : "null")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{Tag} LogViewSnapshot error: {ex}");
            }
        }

        /// <summary>
        /// Reads all known <c>MatchingProgress</c> fields from <paramref name="mp"/> and
        /// writes a snapshot to <c>XMLogging</c>. Uses the cached pointer when
        /// <paramref name="mp"/> is 0.
        /// </summary>
        public static void LogSnapshot(ulong mp = 0)
        {
            if (mp == 0)
            {
                lock (_lock)
                    mp = _cachedMatchingProgress;
            }

            if (!mp.IsValidVirtualAddress())
            {
                XMLogging.WriteLine($"{Tag} LogSnapshot — no valid MatchingProgress pointer.");
                return;
            }

            try
            {
                var currentStage        = (Enums.EMatchingStage)     Memory.ReadValue<int>  (mp + Offsets.MatchingProgress.CurrentStage,        useCache: false);
                var currentStageGroup   = (Enums.EMatchingStageGroup) Memory.ReadValue<int>  (mp + Offsets.MatchingProgress.CurrentStageGroup,   useCache: false);
                var stageProgress       = Memory.ReadValue<float>(mp + Offsets.MatchingProgress.CurrentStageProgress,                            useCache: false);
                var estimateTime        = Memory.ReadValue<int>  (mp + Offsets.MatchingProgress.EstimateTime,                                    useCache: false);
                var isAbortAvailable    = Memory.ReadValue<bool> (mp + Offsets.MatchingProgress.IsAbortAvailable,                                useCache: false);
                var blockAbortDuration  = Memory.ReadValue<int>  (mp + Offsets.MatchingProgress.BlockAbortAbilityDurationSeconds,                useCache: false);
                var showAbortPopup      = Memory.ReadValue<bool> (mp + Offsets.MatchingProgress.ShowAbortConfirmationPopup,                      useCache: false);
                var abortRequested      = Memory.ReadValue<bool> (mp + Offsets.MatchingProgress.IsMatchingAbortRequested,                        useCache: false);
                var canProcessStages    = Memory.ReadValue<bool> (mp + Offsets.MatchingProgress.CanProcessServerStages,                          useCache: false);
                var lastDelayedStage    = (Enums.EMatchingStage)      Memory.ReadValue<int>  (mp + Offsets.MatchingProgress.LastMemorizedDelayedStage,         useCache: false);
                var lastDelayedProgress = Memory.ReadValue<float>(mp + Offsets.MatchingProgress.LastMemorizedDelayedStageProgress,               useCache: false);

                XMLogging.WriteLine(
                    $"{Tag} Snapshot @ 0x{mp:X} | " +
                    $"Stage={currentStage}({(int)currentStage}) Group={currentStageGroup}({(int)currentStageGroup}) " +
                    $"Progress={stageProgress:F3} EstimateTime={estimateTime}s | " +
                    $"LastDelayedStage={lastDelayedStage}({(int)lastDelayedStage}) LastDelayedProgress={lastDelayedProgress:F3} | " +
                    $"IsAbortAvailable={isAbortAvailable} BlockAbortDuration={blockAbortDuration}s " +
                    $"ShowAbortPopup={showAbortPopup} AbortRequested={abortRequested} " +
                    $"CanProcessStages={canProcessStages}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{Tag} LogSnapshot error: {ex}");
            }
        }
    }
}
