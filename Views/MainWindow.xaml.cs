using AngleSharp.Dom;
using DocumentFormat.OpenXml.Packaging;
using HtmlToOpenXml;
using MaterialDesignThemes.Wpf;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Scriban;
using System;
using System.Collections.Generic;

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FishNovel
{
    public partial class MainWindow : Window
    {
        private bool _isWebViewInitialized = false;

        private CancellationTokenSource? _operationCancellation;

        private ObservableCollection<QuestionItem> Questions { get; } =
            new ObservableCollection<QuestionItem>();

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            InitializeQuestions();

            QuestionList.ItemsSource = Questions;

            InitializeWebView();
        }

        // ============================================================
        // 题目模型
        // ============================================================

        

        private void InitializeQuestions()
        {
            Questions.Add(new QuestionItem
            {
                Text = "结合全文，简析文章开头环境描写的作用。",
                Score = "6"
            });

            Questions.Add(new QuestionItem
            {
                Text = "请简要分析文中主要人物的心理变化过程。",
                Score = "6"
            });

            Questions.Add(new QuestionItem
            {
                Text = "结合文本，谈谈你对小说结尾内容的理解。",
                Score = "6"
            });
        }

        // ============================================================
        // WebView2
        // ============================================================

        private async void InitializeWebView()
        {
            try
            {
                await Preview.EnsureCoreWebView2Async(null);

                _isWebViewInitialized = true;

                await RenderEngineAsync();
            }
            catch (Exception ex)
            {
                ToastMsg($"预览初始化失败：{ex.Message}");
            }
        }

        // ============================================================
        // TXT 异步导入
        // ============================================================

        private async void BtnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                Title = "选择小说 TXT 文件"
            };

            if (dialog.ShowDialog() != true)
                return;

            _operationCancellation?.Cancel();
            _operationCancellation = new CancellationTokenSource();

            try
            {
                SetBusy(true, "正在读取小说……", 0);

                TxtFilePath.Text = dialog.FileName;

                string content = await ReadTextFileAsync(
                    dialog.FileName,
                    _operationCancellation.Token);

                TxtNovelContent.Text = content;

                ToastMsg("小说文件导入成功！");
            }
            catch (OperationCanceledException)
            {
                ToastMsg("操作已取消。");
            }
            catch (Exception ex)
            {
                ToastMsg($"读取失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task<string> ReadTextFileAsync(
    string fileName,
    CancellationToken cancellationToken)
        {
            FileInfo fileInfo = new FileInfo(fileName);

            long totalBytes = fileInfo.Length;

            byte[] data;

            // ============================================================
            // 异步读取文件原始字节
            // ============================================================

            using (FileStream stream = new FileStream(
                fileName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                data = new byte[stream.Length];

                int totalRead = 0;

                while (totalRead < data.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int read = await stream.ReadAsync(
                        data,
                        totalRead,
                        data.Length - totalRead,
                        cancellationToken);

                    if (read == 0)
                        break;

                    totalRead += read;

                    if (totalBytes > 0)
                    {
                        double progress =
                            totalRead * 100.0 / totalBytes;

                        SetProgress(
                            progress,
                            "正在读取小说……");
                    }
                }

                if (totalRead != data.Length)
                {
                    Array.Resize(
                        ref data,
                        totalRead);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // ============================================================
            // 1. 优先检查 BOM
            // ============================================================

            Encoding? bomEncoding = DetectBomEncoding(data);

            if (bomEncoding != null)
            {
                int bomLength = GetBomLength(data);

                string text = bomEncoding.GetString(
                    data,
                    bomLength,
                    data.Length - bomLength);

                SetProgress(
                    100,
                    $"读取完成 · 编码：{GetEncodingName(bomEncoding)}");

                return text;
            }

            // ============================================================
            // 2. 无 BOM：尝试 UTF-8
            // ============================================================

            Encoding utf8 = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);

            string? utf8Text = null;

            try
            {
                utf8Text = utf8.GetString(data);
            }
            catch (DecoderFallbackException)
            {
                // 不是有效 UTF-8，继续尝试 GB18030
            }

            // ============================================================
            // 3. 尝试 GB18030
            // ============================================================

            Encoding gb18030 = Encoding.GetEncoding(
                54936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ReplacementFallback);

            string gb18030Text =
                gb18030.GetString(data);

            // ============================================================
            // 4. 判断 UTF-8 和 GB18030 哪个更合理
            // ============================================================

            string result;
            string encodingName;

            if (utf8Text != null)
            {
                int utf8Suspicious =
                    CountSuspiciousCharacters(utf8Text);

                int gb18030Suspicious =
                    CountSuspiciousCharacters(gb18030Text);

                if (utf8Suspicious <= gb18030Suspicious)
                {
                    result = utf8Text;
                    encodingName = "UTF-8";
                }
                else
                {
                    result = gb18030Text;
                    encodingName = "GB18030";
                }
            }
            else
            {
                result = gb18030Text;
                encodingName = "GB18030";
            }

            SetProgress(
                100,
                $"读取完成 · 编码：{encodingName}");

            return result;
        }


        // ============================================================
        // 检测 BOM
        // ============================================================

        private Encoding? DetectBomEncoding(byte[] data)
        {
            // UTF-32 Little Endian
            if (data.Length >= 4 &&
                data[0] == 0xFF &&
                data[1] == 0xFE &&
                data[2] == 0x00 &&
                data[3] == 0x00)
            {
                return new UTF32Encoding(
                    bigEndian: false,
                    byteOrderMark: false);
            }

            // UTF-32 Big Endian
            if (data.Length >= 4 &&
                data[0] == 0x00 &&
                data[1] == 0x00 &&
                data[2] == 0xFE &&
                data[3] == 0xFF)
            {
                return new UTF32Encoding(
                    bigEndian: true,
                    byteOrderMark: false);
            }

            // UTF-8
            if (data.Length >= 3 &&
                data[0] == 0xEF &&
                data[1] == 0xBB &&
                data[2] == 0xBF)
            {
                return new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false);
            }

            // UTF-16 Little Endian
            if (data.Length >= 2 &&
                data[0] == 0xFF &&
                data[1] == 0xFE)
            {
                return Encoding.Unicode;
            }

            // UTF-16 Big Endian
            if (data.Length >= 2 &&
                data[0] == 0xFE &&
                data[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode;
            }

            return null;
        }


        // ============================================================
        // 获取 BOM 长度
        // ============================================================

        private int GetBomLength(byte[] data)
        {
            // UTF-32 LE
            if (data.Length >= 4 &&
                data[0] == 0xFF &&
                data[1] == 0xFE &&
                data[2] == 0x00 &&
                data[3] == 0x00)
            {
                return 4;
            }

            // UTF-32 BE
            if (data.Length >= 4 &&
                data[0] == 0x00 &&
                data[1] == 0x00 &&
                data[2] == 0xFE &&
                data[3] == 0xFF)
            {
                return 4;
            }

            // UTF-8
            if (data.Length >= 3 &&
                data[0] == 0xEF &&
                data[1] == 0xBB &&
                data[2] == 0xBF)
            {
                return 3;
            }

            // UTF-16 LE / BE
            if (data.Length >= 2 &&
                ((data[0] == 0xFF && data[1] == 0xFE) ||
                 (data[0] == 0xFE && data[1] == 0xFF)))
            {
                return 2;
            }

            return 0;
        }


        // ============================================================
        // 判断文本中是否存在明显乱码
        // ============================================================

        private int CountSuspiciousCharacters(string text)
        {
            int count = 0;

            foreach (char c in text)
            {
                // Unicode 替换字符：�
                if (c == '\uFFFD')
                {
                    count++;
                    continue;
                }

                // 非正常控制字符
                if (char.IsControl(c) &&
                    c != '\r' &&
                    c != '\n' &&
                    c != '\t')
                {
                    count++;
                }
            }

            return count;
        }


        // ============================================================
        // 获取编码名称
        // ============================================================

        private string GetEncodingName(Encoding encoding)
        {
            if (encoding is UTF8Encoding)
                return "UTF-8";

            if (encoding == Encoding.Unicode)
                return "UTF-16 LE";

            if (encoding == Encoding.BigEndianUnicode)
                return "UTF-16 BE";

            if (encoding is UTF32Encoding)
                return "UTF-32";

            return encoding.WebName;
        }

        // ============================================================
        // 文本变化
        // ============================================================

        private async void TxtNovelContent_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            await RenderEngineAsync();
        }

        private async void RealtimeUpdate_Changed(
            object sender,
            RoutedEventArgs e)
        {
            await RenderEngineAsync();
        }

        private async void Question_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            await RenderEngineAsync();
        }

        // ============================================================
        // 添加 / 删除题目
        // ============================================================

        private async void BtnAddQuestion_Click(
            object sender,
            RoutedEventArgs e)
        {
            Questions.Add(new QuestionItem
            {
                Text = "请结合文本内容，分析________。",
                Score = "6"
            });

            await RenderEngineAsync();
        }

        private async void BtnDeleteQuestion_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is Button button &&
                button.DataContext is QuestionItem question)
            {
                Questions.Remove(question);

                await RenderEngineAsync();
            }
        }

        // ============================================================
        // 核心：文本清洗
        // ============================================================

        private string CleanNovelText(
    string source,
    bool smartLineBreak,
    string chapterRules)
        {
            if (string.IsNullOrWhiteSpace(source))
                return "";

            source = source
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");

            if (!smartLineBreak)
                return source.Trim();

            string[] lines = source.Split('\n');

            StringBuilder result = new StringBuilder();
            StringBuilder currentParagraph = new StringBuilder();

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph(currentParagraph, result);
                    continue;
                }

                // 自动识别章节标题时，需要保留章节标题和正文之间的换行
                if (IsChapterTitle(line, chapterRules))
                {
                    FlushParagraph(currentParagraph, result);

                    if (result.Length > 0)
                        result.Append("\n\n");

                    result.Append(line);
                    result.Append("\n\n");

                    continue;
                }

                // 普通换行直接拼接
                currentParagraph.Append(line);
            }

            FlushParagraph(currentParagraph, result);

            return result.ToString().Trim();
        }

        private void FlushParagraph(
            StringBuilder paragraph,
            StringBuilder result)
        {
            if (paragraph.Length == 0)
                return;

            if (result.Length > 0 &&
                !result.ToString().EndsWith("\n\n"))
            {
                result.Append("\n\n");
            }

            result.Append(paragraph.ToString().Trim());

            paragraph.Clear();
        }

        // ============================================================
        // 判断章节标题
        // ============================================================
        private ChapterSplitMode GetChapterSplitMode()
        {
            if (RdoParagraphChapter.IsChecked == true)
                return ChapterSplitMode.ParagraphCount;

            if (RdoNoChapter.IsChecked == true)
                return ChapterSplitMode.None;

            return ChapterSplitMode.Auto;
        }

        private int GetParagraphsPerChapter()
        {
            if (!int.TryParse(TxtParagraphsPerChapter.Text, out int count))
                return 20;

            if (count <= 0)
                return 20;

            return count;
        }
        private void ChapterSplitMode_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtParagraphsPerChapter == null)
                return;

            TxtParagraphsPerChapter.IsEnabled =
                RdoParagraphChapter.IsChecked == true;
        }
        private async void TxtParagraphsPerChapter_TextChanged(
    object sender,
    TextChangedEventArgs e)
        {
            if (!_isWebViewInitialized)
                return;

            await RenderEngineAsync();
        }
        private bool IsChapterTitle(
    string line,
    string chapterRules)
        {
            if (string.IsNullOrWhiteSpace(line) ||
                string.IsNullOrWhiteSpace(chapterRules))
            {
                return false;
            }

            string[] rules = chapterRules
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            foreach (string rule in rules)
            {
                if (line.StartsWith(
                    rule,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // ============================================================
        // 章节解析
        // ============================================================

        private List<ChapterModel> SplitChapters(
    string cleanedText,
    string chapterRules,
    ChapterSplitMode splitMode,
    int paragraphsPerChapter)
        {
            switch (splitMode)
            {
                case ChapterSplitMode.ParagraphCount:
                    return SplitByParagraphCount(
                        cleanedText,
                        paragraphsPerChapter);

                case ChapterSplitMode.None:
                    return SplitAsSingleChapter(
                        cleanedText);

                case ChapterSplitMode.Auto:
                default:
                    return SplitByChapterRules(
                        cleanedText,
                        chapterRules);
            }
        }
        private List<ChapterModel> SplitAsSingleChapter(
    string cleanedText)
        {
            List<ChapterModel> chapters =
                new List<ChapterModel>();

            if (string.IsNullOrWhiteSpace(cleanedText))
                return chapters;

            ChapterModel chapter = new ChapterModel
            {
                Title = "（一）"
            };

            string[] paragraphs = cleanedText
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            foreach (string paragraph in paragraphs)
            {
                chapter.Paragraphs.Add(paragraph);
            }

            chapters.Add(chapter);

            return chapters;
        }

        private List<ChapterModel> SplitByParagraphCount(
    string cleanedText,
    int paragraphsPerChapter)
        {
            List<ChapterModel> chapters = new List<ChapterModel>();

            if (string.IsNullOrWhiteSpace(cleanedText))
                return chapters;

            if (paragraphsPerChapter <= 0)
                paragraphsPerChapter = 20;

            string[] paragraphs = cleanedText
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            if (paragraphs.Length == 0)
                return chapters;

            int chapterNumber = 1;

            for (
                int start = 0;
                start < paragraphs.Length;
                start += paragraphsPerChapter)
            {
                ChapterModel chapter = new ChapterModel
                {
                    // 正式阅读练习使用中文序号
                    Title = $"（{ToChineseNumber(chapterNumber)}）"
                };

                int end = Math.Min(
                    start + paragraphsPerChapter,
                    paragraphs.Length);

                for (int i = start; i < end; i++)
                {
                    chapter.Paragraphs.Add(paragraphs[i]);
                }

                chapters.Add(chapter);

                chapterNumber++;
            }

            return chapters;
        }
        private string ToChineseNumber(int number)
        {
            string[] digits =
            {
        "零", "一", "二", "三", "四",
        "五", "六", "七", "八", "九"
    };

            if (number <= 0)
                return "零";

            if (number < 10)
                return digits[number];

            if (number < 20)
                return "十" + (number % 10 == 0
                    ? ""
                    : digits[number % 10]);

            if (number < 100)
            {
                return digits[number / 10]
                    + "十"
                    + (number % 10 == 0
                        ? ""
                        : digits[number % 10]);
            }

            // 一般不会切出超过99篇，这里提供一个简单兜底
            return number.ToString();
        }

        private List<ChapterModel> SplitByChapterRules(
    string cleanedText,
    string chapterRules)
        {
            List<ChapterModel> chapters = new List<ChapterModel>();

            if (string.IsNullOrWhiteSpace(cleanedText))
                return chapters;

            string[] lines = cleanedText
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n');

            ChapterModel? currentChapter = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (IsChapterTitle(line, chapterRules))
                {
                    currentChapter = new ChapterModel
                    {
                        Title = line
                    };

                    chapters.Add(currentChapter);
                    continue;
                }

                if (currentChapter == null)
                {
                    currentChapter = new ChapterModel
                    {
                        Title = "正文"
                    };

                    chapters.Add(currentChapter);
                }

                currentChapter.Paragraphs.Add(line);
            }

            return chapters;
        }

        // ============================================================
        // HTML 渲染
        // ============================================================

        private async Task RenderEngineAsync()
        {
            if (!_isWebViewInitialized ||
                Preview.CoreWebView2 == null)
            {
                return;
            }

            string source = TxtNovelContent.Text;

            string title =
                string.IsNullOrWhiteSpace(TxtExamTitle.Text)
                    ? "小说阅读专练"
                    : TxtExamTitle.Text.Trim();

            string subtitle =
                string.IsNullOrWhiteSpace(TxtSubtitle.Text)
                    ? "现代文阅读与文本理解训练"
                    : TxtSubtitle.Text.Trim();

            bool smartLineBreak =
                ChkSmartLineBreak.IsChecked == true;

            string chapterRules =
                TxtChapterRules.Text;

            ChapterSplitMode splitMode =
                GetChapterSplitMode();

            int paragraphsPerChapter =
                GetParagraphsPerChapter();

            List<QuestionItem> questions = Questions
                .Where(q => !string.IsNullOrWhiteSpace(q.Text))
                .Select(q => new QuestionItem
                {
                    Text = q.Text,
                    Score = q.Score
                })
                .ToList();

            string cleanedText = await Task.Run(() =>
                CleanNovelText(
                    source,
                    smartLineBreak,
                    chapterRules));

            List<ChapterModel> chapters = await Task.Run(() =>
                SplitChapters(
                    cleanedText,
                    chapterRules,
                    splitMode,
                    paragraphsPerChapter));

            AssignQuestionsToChapters(
                chapters,
                questions);

            string html = await Task.Run(() =>
                BuildHtml(
                    title,
                    subtitle,
                    chapters));

            Preview.NavigateToString(html);
        }
        // ============================================================
        // 随机将用户添加的题目分配到各章节
        // 每章优先分配 2～3 道题
        // ============================================================

        // ============================================================
        // 随机将用户添加的题目分配到各章节
        //
        // 规则：
        // 1. 每章随机分配 2～3 道题
        // 2. 用户题目可以重复使用
        // 3. 同一章节内优先不重复
        // 4. 题目不足 2 道时，允许同章重复
        // ============================================================

        private void AssignQuestionsToChapters(
            List<ChapterModel> chapters,
            List<QuestionItem> questions)
        {
            if (chapters == null ||
                chapters.Count == 0 ||
                questions == null ||
                questions.Count == 0)
            {
                return;
            }

            // --------------------------------------------------------
            // 清空原有分配
            // --------------------------------------------------------

            foreach (ChapterModel chapter in chapters)
            {
                chapter.Questions.Clear();
            }

            Random random = new Random();

            // --------------------------------------------------------
            // 为每一章分别随机分配 2～3 道题
            // --------------------------------------------------------

            foreach (ChapterModel chapter in chapters)
            {
                // 每章随机决定是 2 题还是 3 题
                int questionCount =
                    random.Next(2, 4);

                // ----------------------------------------------------
                // 用户题目 >= 2：
                // 同一章节内尽量不重复
                // ----------------------------------------------------

                if (questions.Count >= 2)
                {
                    List<QuestionItem> shuffled =
                        questions
                            .OrderBy(x => random.Next())
                            .ToList();

                    for (int i = 0;
                         i < questionCount;
                         i++)
                    {
                        // 如果用户只有 2 道题，而本章需要 3 道，
                        // 第 3 道允许重复。
                        chapter.Questions.Add(
                            shuffled[i % shuffled.Count]);
                    }
                }

                // ----------------------------------------------------
                // 用户只有 1 道题：
                // 只能重复使用这一道
                // ----------------------------------------------------

                else
                {
                    for (int i = 0;
                         i < questionCount;
                         i++)
                    {
                        chapter.Questions.Add(
                            questions[0]);
                    }
                }
            }
        }
        // ============================================================
        // Scriban HTML 模板
        // ============================================================

        private string BuildHtml(
string title,
string subtitle,
List<ChapterModel> chapters)
        {
            const string templateText = @"
<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">

<style>

* {
    box-sizing: border-box;
}

html {
    background: #e5e5e5;
}

body {
    margin: 0;
    padding: 24px 0 50px 0;
    background: #e5e5e5;
    color: #222222;
    font-family: ""SimSun"", ""宋体"", serif;
    font-size: 15px;
    line-height: 1.9;
}

/* ============================================================
   A4 页面
   ============================================================ */

#document {
    width: 210mm;
    margin: 0 auto;
}

.pdf-page {
    position: relative;

    width: 210mm;
    height: 297mm;

    margin: 0 auto 24px auto;

    background: #ffffff;

    padding: 20mm 18mm 20mm 18mm;

    overflow: hidden;

    box-shadow: 0 2px 10px rgba(0, 0, 0, 0.12);
}

.page-content {
    width: 100%;

    /*
     * 页面底部给页码预留空间。
     */
    height: calc(100% - 8mm);

    overflow: hidden;
}

/* ============================================================
   封面
   ============================================================ */

.cover {
    text-align: center;
    padding: 28px 0 35px 0;
    border-bottom: 1px solid #444;
    margin-bottom: 30px;
}

.title {
    font-family: ""SimHei"", ""黑体"", sans-serif;
    font-size: 26px;
    font-weight: bold;
    letter-spacing: 2px;
}

.subtitle {
    margin-top: 10px;
    color: #555;
    font-size: 14px;
}

/* ============================================================
   章节
   ============================================================ */

.chapter {
    margin-top: 34px;
}

.chapter:first-child {
    margin-top: 0;
}

.chapter-title {
    font-family: ""KaiTi"", ""楷体"", serif;
    font-size: 21px;
    font-weight: bold;

    text-align: center;

    margin:
        0 0
        20px 0;

    padding:
        0 0
        8px 0;
}

.paragraph {
    font-family: ""KaiTi"", ""楷体"", serif;

    text-indent: 2em;

    margin:
        0 0
        12px 0;

    text-align: justify;
}

/* ============================================================
   阅读思考
   ============================================================ */

.chapter-questions {
    margin-top: 32px;
}

.chapter-questions-title {
    font-family: ""SimSun"", ""宋体"", serif;

    font-size: 18px;
    font-weight: bold;

    margin-bottom: 18px;

    border-bottom: 1px solid #444;

    padding-bottom: 7px;
}

.question {
    margin-bottom: 20px;

    break-inside: avoid;
}

.question-text {
    font-family: ""SimSun"", ""宋体"", serif;

    font-weight: bold;

    margin-bottom: 10px;
}

.answer-line {
    height: 30px;

    border-bottom: 1px solid #777;

    margin-left: 1em;

    margin-bottom: 2px;
}

/* ============================================================
   页码
   ============================================================ */

.page-number {
    position: absolute;

    left: 18mm;
    right: 18mm;
    bottom: 7mm;

    height: 7mm;

    text-align: center;

    font-family: ""SimSun"", ""宋体"", serif;

    font-size: 10pt;

    line-height: 7mm;

    color: #666;
}

/* ============================================================
   原始内容
   ============================================================ */

#source {
    display: none;
}

/* ============================================================
   屏幕预览
   ============================================================ */

@media screen {

    .pdf-page {
        display: block;
    }

}

/* ============================================================
   PDF 打印
   ============================================================ */

@page {
    size: A4 portrait;
    margin: 0;
}

@media print {

    html,
    body {
        background: #ffffff;
    }

    body {
        padding: 0;
        margin: 0;
    }

    #document {
        width: 210mm;
        margin: 0;
    }

    .pdf-page {
        width: 210mm;
        height: 297mm;

        margin: 0;

        box-shadow: none;

        page-break-after: always;
        break-after: page;

        overflow: hidden;
    }

    .pdf-page:last-child {
        page-break-after: auto;
        break-after: auto;
    }

    #source {
        display: none;
    }

}

</style>
</head>

<body>

<div id=""document""></div>

<div id=""source"">

    <div class=""cover"">
        <div class=""title"">{{ title }}</div>
        <div class=""subtitle"">{{ subtitle }}</div>
    </div>

    {{ for chapter in chapters }}

    <section class=""chapter"">

        <div class=""chapter-title"">
            {{ chapter.title }}
        </div>

        {{ for paragraph in chapter.paragraphs }}

        <p class=""paragraph"">
            {{ paragraph }}
        </p>

        {{ end }}

        {{ if chapter.questions.size > 0 }}

        <div class=""chapter-questions"">

            <div class=""chapter-questions-title"">
                阅读思考
            </div>

            {{ for question in chapter.questions }}

            <div class=""question"">

                <div class=""question-text"">
                    {{ question.index }}. {{ question.text }}
                    {{ if question.score }}
                    （{{ question.score }}分）
                    {{ end }}
                </div>

                <div class=""answer-line""></div>
                <div class=""answer-line""></div>
                <div class=""answer-line""></div>

            </div>

            {{ end }}

        </div>

        {{ end }}

    </section>

    {{ end }}

</div>

<script>

(function () {

    /*
     * ============================================================
     * 创建页面
     * ============================================================
     */
    function createPage() {

        const documentContainer =
            document.getElementById(""document"");

        const page =
            document.createElement(""div"");

        page.className =
            ""pdf-page"";

        const content =
            document.createElement(""div"");

        content.className =
            ""page-content"";

        const pageNumber =
            document.createElement(""div"");

        pageNumber.className =
            ""page-number"";

        page.appendChild(content);
        page.appendChild(pageNumber);

        documentContainer.appendChild(page);

        return page;
    }


    /*
     * ============================================================
     * 判断页面是否溢出
     * ============================================================
     */
    function isOverflowing(content) {

        return (
            content.scrollHeight >
            content.clientHeight + 1
        );
    }


    /*
     * ============================================================
     * 将完整元素加入当前页面
     *
     * 注意：
     *
     * 这里只负责“完整元素”。
     *
     * 如果放不下：
     *     当前页什么都不动
     *     创建下一页
     *     再放进去
     *
     * 不存在章节结束换页。
     * 不存在题目结束换页。
     * ============================================================
     */
    function addBlock(
        element,
        currentPage)
    {
        let content =
            currentPage.querySelector(
                "".page-content""
            );

        /*
         * 先尝试当前页。
         */
        content.appendChild(element);

        if (
            !isOverflowing(content)
        ) {

            return currentPage;
        }

        /*
         * 当前页放不下。
         *
         * 撤销。
         */
        content.removeChild(element);


        /*
         * 创建下一页。
         */
        currentPage =
            createPage();

        content =
            currentPage.querySelector(
                "".page-content""
            );

        content.appendChild(element);

        return currentPage;
    }


    /*
     * ============================================================
     * 添加可以跨页的正文段落
     *
     * 一个段落放不下时：
     *
     *     当前页填到最后
     *     剩余文字进入下一页
     *
     * 不允许一个长段落制造大片空白。
     * ============================================================
     */
    function addParagraph(
        paragraph,
        currentPage)
    {
        let remaining =
            paragraph.textContent || """";

        if (
            remaining.trim().length === 0
        ) {

            return currentPage;
        }


        while (
            remaining.length > 0
        ) {

            let content =
                currentPage.querySelector(
                    "".page-content""
                );


            /*
             * ----------------------------------------------------
             * 先尝试完整段落。
             * ----------------------------------------------------
             */
            const whole =
                paragraph.cloneNode(false);

            whole.textContent =
                remaining;

            content.appendChild(whole);

            if (
                !isOverflowing(content)
            ) {

                /*
                 * 完整段落放下了。
                 */
                break;
            }

            /*
             * 撤销完整段落。
             */
            content.removeChild(whole);


            /*
             * ----------------------------------------------------
             * 二分寻找当前页面最多能放多少文字。
             * ----------------------------------------------------
             */
            let low = 1;
            let high =
                remaining.length;

            let best = 0;

            while (
                low <= high
            ) {

                const middle =
                    Math.floor(
                        (low + high) / 2
                    );

                const test =
                    paragraph.cloneNode(false);

                test.textContent =
                    remaining.substring(
                        0,
                        middle
                    );

                content.appendChild(test);

                const overflow =
                    isOverflowing(content);

                content.removeChild(test);

                if (
                    !overflow
                ) {

                    best =
                        middle;

                    low =
                        middle + 1;
                }
                else {

                    high =
                        middle - 1;
                }
            }


            /*
             * ----------------------------------------------------
             * 当前页面连一个字符都放不下。
             *
             * 创建下一页。
             * ----------------------------------------------------
             */
            if (
                best <= 0
            ) {

                currentPage =
                    createPage();

                continue;
            }


            /*
             * ----------------------------------------------------
             * 正式加入能够容纳的部分。
             * ----------------------------------------------------
             */
            const part =
                paragraph.cloneNode(false);

            part.textContent =
                remaining.substring(
                    0,
                    best
                );

            content.appendChild(part);


            /*
             * 剩余文字。
             */
            remaining =
                remaining.substring(
                    best
                );


            /*
             * ----------------------------------------------------
             * 还有文字就继续下一页。
             * ----------------------------------------------------
             */
            if (
                remaining.length > 0
            ) {

                currentPage =
                    createPage();
            }
        }

        return currentPage;
    }


    /*
     * ============================================================
     * 添加章节标题
     *
     * 章节标题本身是完整元素。
     *
     * 如果当前位置放不下：
     *     只把标题移动到下一页。
     *
     * 注意：
     * 这并不意味着章节结束后换页。
     * ============================================================
     */
    function addChapterTitle(
        title,
        currentPage)
    {
        return addBlock(
            title.cloneNode(true),
            currentPage
        );
    }


    /*
     * ============================================================
     * 添加阅读思考标题
     *
     * 阅读思考标题也是普通完整元素。
     *
     * 如果当前页放得下，就绝不换页。
     * ============================================================
     */
    function addQuestionsTitle(
        title,
        currentPage)
    {
        return addBlock(
            title.cloneNode(true),
            currentPage
        );
    }


    /*
     * ============================================================
     * 添加一道题
     *
     * 一道题作为一个整体。
     *
     * 题目 + 三条答题线必须保持在一起。
     *
     * 只有整道题真的放不下时才换页。
     * ============================================================
     */
    function addQuestion(
        question,
        currentPage)
    {
        return addBlock(
            question.cloneNode(true),
            currentPage
        );
    }


    /*
     * ============================================================
     * 处理章节
     *
     * 非常重要：
     *
     * 这里没有任何：
     *
     *     “章节结束后换页”
     *
     *     “文章结束后换页”
     *
     *     “题目结束后换页”
     *
     *     “下一章节必须换页”
     *
     * 所有内容都是从当前位置继续往下排。
     * ============================================================
     */
    function addChapter(
        chapter,
        currentPage)
    {
        const children =
            Array.from(
                chapter.children
            );


        for (
            let i = 0;
            i < children.length;
            i++
        ) {

            const child =
                children[i];


            /*
             * ----------------------------------------------------
             * 章节标题
             * ----------------------------------------------------
             */
            if (
                child.classList.contains(
                    ""chapter-title""
                )
            ) {

                currentPage =
                    addChapterTitle(
                        child,
                        currentPage
                    );

                continue;
            }


            /*
             * ----------------------------------------------------
             * 正文段落
             *
             * 可以跨页。
             * ----------------------------------------------------
             */
            if (
                child.classList.contains(
                    ""paragraph""
                )
            ) {

                currentPage =
                    addParagraph(
                        child.cloneNode(true),
                        currentPage
                    );

                continue;
            }


            /*
             * ----------------------------------------------------
             * 阅读思考
             * ----------------------------------------------------
             */
            if (
                child.classList.contains(
                    ""chapter-questions""
                )
            ) {

                const questionChildren =
                    Array.from(
                        child.children
                    );


                for (
                    let q = 0;
                    q < questionChildren.length;
                    q++
                ) {

                    const questionElement =
                        questionChildren[q];


                    /*
                     * 阅读思考标题
                     */
                    if (
                        questionElement.classList.contains(
                            ""chapter-questions-title""
                        )
                    ) {

                        currentPage =
                            addQuestionsTitle(
                                questionElement,
                                currentPage
                            );

                        continue;
                    }


                    /*
                     * 单独一道题
                     */
                    if (
                        questionElement.classList.contains(
                            ""question""
                        )
                    ) {

                        currentPage =
                            addQuestion(
                                questionElement,
                                currentPage
                            );

                        continue;
                    }


                    /*
                     * 其他内容正常处理。
                     */
                    currentPage =
                        addBlock(
                            questionElement.cloneNode(true),
                            currentPage
                        );
                }

                /*
                 * =================================================
                 * 关键：
                 *
                 * 这里直接 continue。
                 *
                 * 绝对不能 createPage()。
                 *
                 * 所以下一个章节会紧接着继续使用当前页面。
                 * =================================================
                 */
                continue;
            }


            /*
             * ----------------------------------------------------
             * 其他未知元素
             * ----------------------------------------------------
             */
            currentPage =
                addBlock(
                    child.cloneNode(true),
                    currentPage
                );
        }


        /*
         * ========================================================
         * 关键：
         *
         * 章节结束以后直接返回当前页面。
         *
         * 不创建新页面。
         * ========================================================
         */
        return currentPage;
    }


    /*
     * ============================================================
     * 主分页
     * ============================================================
     */
    function paginate() {

        const documentContainer =
            document.getElementById(
                ""document""
            );

        const source =
            document.getElementById(
                ""source""
            );


        /*
         * 清空旧页面。
         */
        documentContainer.innerHTML =
            """";


        /*
         * 创建第一页。
         */
        let currentPage =
            createPage();


        const elements =
            Array.from(
                source.children
            );


        /*
         * ========================================================
         * 严格按照 source 原始顺序处理。
         * ========================================================
         */
        for (
            let i = 0;
            i < elements.length;
            i++
        ) {

            const element =
                elements[i];


            /*
             * 章节。
             */
            if (
                element.classList.contains(
                    ""chapter""
                )
            ) {

                currentPage =
                    addChapter(
                        element,
                        currentPage
                    );

                /*
                 * =================================================
                 * 注意：
                 *
                 * 这里绝对不创建新页面。
                 *
                 * 下一个 chapter 会直接接着当前页面。
                 * =================================================
                 */
                continue;
            }


            /*
             * 其他内容，例如封面。
             */
            if (
                element.classList.contains(
                    ""cover""
                )
            ) {

                currentPage =
                    addBlock(
                        element.cloneNode(true),
                        currentPage
                    );

                continue;
            }


            /*
             * 未知元素也按照普通元素处理。
             */
            currentPage =
                addBlock(
                    element.cloneNode(true),
                    currentPage
                );
        }


        /*
         * ========================================================
         * 删除空页面
         *
         * 理论上第一页不会为空。
         * ========================================================
         */
        const pages =
            Array.from(
                documentContainer.querySelectorAll(
                    "".pdf-page""
                )
            );


        for (
            let i = pages.length - 1;
            i >= 0;
            i--
        ) {

            const content =
                pages[i].querySelector(
                    "".page-content""
                );

            if (
                content &&
                content.children.length === 0
            ) {

                pages[i].remove();
            }
        }


        /*
         * ========================================================
         * 最终页码
         * ========================================================
         */
        const finalPages =
            documentContainer.querySelectorAll(
                "".pdf-page""
            );


        finalPages.forEach(
            function (
                page,
                index)
            {

                const number =
                    page.querySelector(
                        "".page-number""
                    );

                if (number) {

                    number.textContent =
                        ""第 "" +
                        (index + 1) +
                        "" 页"";
                }
            }
        );


        /*
         * ========================================================
         * 告知 WPF 分页完成。
         * ========================================================
         */
        document.body.setAttribute(
            ""data-pagination-complete"",
            ""true""
        );

        document.body.setAttribute(
            ""data-page-count"",
            finalPages.length
        );
    }


    /*
     * ============================================================
     * 等待字体和布局稳定。
     * ============================================================
     */
    function start() {

        if (
            document.fonts &&
            document.fonts.ready
        ) {

            document.fonts.ready.then(
                function () {

                    requestAnimationFrame(
                        function () {

                            requestAnimationFrame(
                                function () {

                                    paginate();
                                }
                            );
                        }
                    );
                }
            );

        }
        else {

            requestAnimationFrame(
                function () {

                    paginate();
                }
            );
        }
    }


    /*
     * ============================================================
     * 启动
     * ============================================================
     */
    if (
        document.readyState ===
        ""loading""
    ) {

        document.addEventListener(
            ""DOMContentLoaded"",
            start
        );

    }
    else {

        start();
    }

})();

</script>

</body>
</html>";

            var chapterModels = chapters
                .Select(c => new
                {
                    title = HtmlEncode(c.Title),

                    paragraphs = c.Paragraphs
                        .Select(HtmlEncode)
                        .ToList(),

                    questions = (c.Questions ?? new List<QuestionItem>())
                        .Select((q, index) => new
                        {
                            index = index + 1,
                            text = HtmlEncode(q.Text),
                            score = HtmlEncode(q.Score)
                        })
                        .ToList()
                })
                .ToList();

            var model = new
            {
                title = HtmlEncode(title),
                subtitle = HtmlEncode(subtitle),
                chapters = chapterModels
            };

            var template = Scriban.Template.Parse(templateText);

            if (template.HasErrors)
            {
                throw new InvalidOperationException(
                    string.Join(
                        Environment.NewLine,
                        template.Messages));
            }

            return template.Render(model);
        }

        // ============================================================
        // HTML 编码
        // ============================================================

        private static string HtmlEncode(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }

        // ============================================================
        // Word 导出
        // ============================================================

        private async void BtnExportDocx_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "Word 文档 (*.docx)|*.docx",
                DefaultExt = ".docx",
                AddExtension = true,
                FileName = "小说阅读专练.docx"
            };

            if (dialog.ShowDialog() != true)
                return;

            _operationCancellation?.Cancel();
            _operationCancellation = new CancellationTokenSource();

            try
            {
                SetBusy(true, "正在准备 Word 文档……", 0);

                // =========================================================
                // 1. UI 线程读取所有控件
                // =========================================================

                string source = TxtNovelContent.Text;

                string title = string.IsNullOrWhiteSpace(TxtExamTitle.Text)
                    ? "小说阅读专练"
                    : TxtExamTitle.Text.Trim();

                string subtitle = string.IsNullOrWhiteSpace(TxtSubtitle.Text)
                    ? "现代文阅读与文本理解训练"
                    : TxtSubtitle.Text.Trim();

                bool smartLineBreak =
                    ChkSmartLineBreak.IsChecked == true;

                string chapterRules =
                    TxtChapterRules.Text;

                ChapterSplitMode splitMode =
                    GetChapterSplitMode();

                int paragraphsPerChapter =
                    GetParagraphsPerChapter();

                // 复制一份题目数据，避免后台线程访问 ObservableCollection
                List<QuestionItem> questions = Questions
                    .Where(q => !string.IsNullOrWhiteSpace(q.Text))
                    .Select(q => new QuestionItem
                    {
                        Text = q.Text,
                        Score = q.Score
                    })
                    .ToList();

                _operationCancellation.Token.ThrowIfCancellationRequested();

                // =========================================================
                // 2. 清理小说文本
                // =========================================================

                SetProgress(15, "正在整理小说文本……");

                string cleanedText = await Task.Run(
                    () => CleanNovelText(
                        source,
                        smartLineBreak,
                        chapterRules),
                    _operationCancellation.Token);

                _operationCancellation.Token.ThrowIfCancellationRequested();

                // =========================================================
                // 3. 按用户选择的方式分章
                // =========================================================

                SetProgress(30, "正在划分阅读单元……");

                List<ChapterModel> chapters = await Task.Run(
    () => SplitChapters(
        cleanedText,
        chapterRules,
        splitMode,
        paragraphsPerChapter),
    _operationCancellation.Token);

                _operationCancellation.Token.ThrowIfCancellationRequested();

                // =========================================================
                // 4. 将用户题目随机分配到各阅读章节
                // =========================================================

                AssignQuestionsToChapters(
                    chapters,
                    questions);

                _operationCancellation.Token.ThrowIfCancellationRequested();

                // =========================================================
                // 5. 生成 HTML
                // =========================================================

                SetProgress(45, "正在生成阅读专练内容……");

                string html = BuildHtml(
                    title,
                    subtitle,
                    chapters);

                _operationCancellation.Token.ThrowIfCancellationRequested();

                // =========================================================
                // 5. HTML → DOCX
                // =========================================================

                SetProgress(60, "正在生成 Word 文档……");

                await Task.Run(
                    () => ExportHtmlToDocx(
                        html,
                        dialog.FileName,
                        _operationCancellation.Token),
                    _operationCancellation.Token);

                _operationCancellation.Token.ThrowIfCancellationRequested();

                // =========================================================
                // 6. 完成
                // =========================================================

                SetProgress(100, "导出完成");

                ToastMsg("Word 阅读专练导出成功！");
            }
            catch (OperationCanceledException)
            {
                ToastMsg("导出已取消。");
            }
            catch (Exception ex)
            {
                ToastMsg($"导出失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void BtnExportPdf_Click(
    object sender,
    RoutedEventArgs e)
        {
            if (!_isWebViewInitialized ||
                Preview.CoreWebView2 == null)
            {
                ToastMsg("预览尚未初始化完成。");
                return;
            }
            string fileName = TxtExamTitle.Text + ".pdf";
            SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "PDF 文档 (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = fileName
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                SetBusy(true, "正在生成 PDF……", 0);

                CoreWebView2PrintSettings settings =
                    Preview.CoreWebView2.Environment
                        .CreatePrintSettings();

                // 使用 CSS / HTML 自己定义的页面布局
                settings.ShouldPrintBackgrounds = true;

                // 不显示浏览器默认页眉页脚
                settings.ShouldPrintHeaderAndFooter = false;

                SetProgress(30, "正在渲染打印页面……");

                bool success =
                    await Preview.CoreWebView2.PrintToPdfAsync(
                        dialog.FileName,
                        settings);

                if (!success)
                {
                    ToastMsg("PDF 生成失败。");
                    return;
                }

                SetProgress(100, "PDF 导出完成");

                ToastMsg("PDF 阅读专练导出成功！");
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                }
                catch (Exception ex)
                { 
                    ToastMsg($"打开所在文件夹失败：{ex.Message}");
                }
            }
            catch (Exception ex)
            {
                ToastMsg($"PDF 导出失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void 
            ExportHtmlToDocx(
            string html,
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using WordprocessingDocument document =
                WordprocessingDocument.Create(
                    fileName,
                    DocumentFormat.OpenXml.WordprocessingDocumentType.Document);

            MainDocumentPart mainPart =
                document.AddMainDocumentPart();

            mainPart.Document =
                new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body());

            HtmlConverter converter =
                new HtmlConverter(mainPart);

            var elements = converter.Parse(html);

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var element in elements)
            {
                mainPart.Document.Body.Append(element);
            }

            mainPart.Document.Save();
        }

        // ============================================================
        // 进度 / Busy
        // ============================================================

        private void SetBusy(
            bool busy,
            string message = "",
            double progress = 0)
        {
            BtnSelectFile.IsEnabled = !busy;
            BtnExportDocx.IsEnabled = !busy;

            ProgressBar.Visibility =
                busy
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

            ProgressText.Text =
                busy
                    ? message
                    : "";

            if (busy)
                ProgressBar.Value = progress;
        }

        private void SetProgress(
            double progress,
            string message)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value =
                    Math.Max(0, Math.Min(100, progress));

                ProgressText.Text = message;
            });
        }

        // ============================================================
        // Toast
        // ============================================================

        public void ToastMsg(
            string msg,
            int duration = 3000)
        {
            Clipboard.SetDataObject(msg);
            Dispatcher.Invoke(() =>
            {
                Border border = new Border
                {
                    Background =
                        new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(
                                "#E6B0B0")),

                    CornerRadius =
                        new CornerRadius(5),

                    Padding =
                        new Thickness(8),

                    Margin =
                        new Thickness(8)
                };

                StackPanel stackPanel =
                    new StackPanel
                    {
                        Orientation =
                            Orientation.Horizontal,

                        HorizontalAlignment =
                            System.Windows.HorizontalAlignment.Center,

                        VerticalAlignment =
                            System.Windows.VerticalAlignment.Center
                    };

                PackIcon packIcon =
                    new PackIcon
                    {
                        Kind =
                            PackIconKind.InfoOutline,

                        Width = 16,
                        Height = 26,

                        Foreground =
                            Brushes.AliceBlue
                    };

                TextBlock textBlock =
                    new TextBlock
                    {
                        Text = msg,

                        Foreground =
                            Brushes.AliceBlue,

                        VerticalAlignment =
                            System.Windows.VerticalAlignment.Center
                    };

                stackPanel.Children.Add(packIcon);
                stackPanel.Children.Add(textBlock);

                border.Child = stackPanel;

                Toast.Children.Add(border);

                DispatcherTimer timer =
                    new DispatcherTimer
                    {
                        Interval =
                            TimeSpan.FromMilliseconds(duration)
                    };

                timer.Tick += (sender, e) =>
                {
                    timer.Stop();

                    if (Toast.Children.Contains(border))
                        Toast.Children.Remove(border);
                };

                timer.Start();
            });
        }
    }

    // ================================================================
    // 章节模型
    // ================================================================

    public class ChapterModel
    {
        public string Title { get; set; } = "";

        public List<string> Paragraphs { get; set; } =
            new List<string>();

        public List<QuestionItem> Questions { get; set; } =
            new List<QuestionItem>();
    }
    public class QuestionItem
    {
        public string Text { get; set; } = "";

        public string Score { get; set; } = "6";
    }
    public enum ChapterSplitMode
    {
        Auto,
        ParagraphCount,
        None
    }
}