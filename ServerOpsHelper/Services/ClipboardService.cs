using System;
using System.Threading;
using System.Windows;

namespace ServerOpsHelper.Services;

/// <summary>
/// 剪贴板封装。
/// <para>
/// Windows 剪贴板是共享资源，被其它进程占用时 <see cref="Clipboard.SetText(string)"/> 会抛
/// <see cref="System.Runtime.InteropServices.COMException"/>，因此这里做少量重试并吞掉异常，
/// 由调用方决定如何提示，避免为了复制一次密码把整个应用搞崩。
/// </para>
/// </summary>
public static class ClipboardService
{
    private const int MaxAttempts = 3;

    /// <summary>
    /// 复制文本到剪贴板
    /// </summary>
    /// <returns>是否复制成功</returns>
    public static bool TryCopy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (Exception)
            {
                if (attempt == MaxAttempts)
                {
                    return false;
                }

                // 剪贴板被占用时短暂等待后重试
                Thread.Sleep(60);
            }
        }

        return false;
    }
}
