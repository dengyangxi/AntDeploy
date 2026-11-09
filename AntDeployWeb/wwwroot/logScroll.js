// 实时日志智能自动滚底：默认吸底，用户手动上滚则暂停自动，滚回底部（阈值内）恢复自动。
// 全部判定在浏览器端完成，避免 Blazor Server 每次 scroll 事件都回服务器；.NET 仅在日志行数变化时调用 toBottomIfPinned。
(function () {
    var THRESHOLD = 40; // 距底小于该像素值视为"在底部"

    function gapToBottom(el) {
        return el.scrollHeight - el.scrollTop - el.clientHeight;
    }

    window.antdeployScroll = {
        // 绑定滚动监听，维护 el 的吸底状态（默认吸底）
        init: function (el) {
            if (!el || el.__antScrollInit) return;
            el.__antScrollInit = true;
            el.__antPinned = true;
            el.addEventListener('scroll', function () {
                el.__antPinned = gapToBottom(el) <= THRESHOLD;
            }, { passive: true });
        },
        // 仅当处于吸底状态时滚到底（用户上滚查看历史时不打扰）
        toBottomIfPinned: function (el) {
            if (!el || el.__antPinned === false) return;
            el.scrollTop = el.scrollHeight;
        },
        // 强制滚到底并恢复吸底（首次渲染 / 新会话开始时用）
        toBottom: function (el) {
            if (!el) return;
            el.__antPinned = true;
            el.scrollTop = el.scrollHeight;
        }
    };
})();
