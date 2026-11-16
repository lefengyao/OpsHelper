using System;
using System.Security.Cryptography;

namespace ServerOpsHelper.Services;

/// <summary>
/// 随机密码生成器（基于 <see cref="RandomNumberGenerator"/>，非伪随机）
/// </summary>
public static class PasswordGenerator
{
    private const string LowerChars = "abcdefghijkmnopqrstuvwxyz";
    private const string UpperChars = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string DigitChars = "23456789";
    private const string SymbolChars = "!@#$%^&*()-_=+[]{}";

    /// <summary>
    /// 生成随机密码，保证至少包含小写、大写、数字、符号各一个
    /// </summary>
    /// <param name="length">长度，小于 8 时按 8 处理</param>
    public static string Generate(int length = 16)
    {
        if (length < 8)
        {
            length = 8;
        }

        var all = LowerChars + UpperChars + DigitChars + SymbolChars;
        var buffer = new char[length];

        var index = 0;
        if (length >= 4)
        {
            buffer[index++] = Pick(LowerChars);
            buffer[index++] = Pick(UpperChars);
            buffer[index++] = Pick(DigitChars);
            buffer[index++] = Pick(SymbolChars);
        }

        while (index < length)
        {
            buffer[index++] = Pick(all);
        }

        Shuffle(buffer);
        return new string(buffer);
    }

    private static char Pick(string charSet) => charSet[RandomNumberGenerator.GetInt32(charSet.Length)];

    /// <summary>
    /// Fisher-Yates 洗牌，避免必选字符总落在前几位
    /// </summary>
    private static void Shuffle(char[] buffer)
    {
        for (var i = buffer.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }
    }
}
