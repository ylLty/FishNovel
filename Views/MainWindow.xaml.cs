using MaterialDesignThemes.Wpf;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FishNovel
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            InitializeWebView();
        }
        private async void InitializeWebView()
        {
            await Preview.EnsureCoreWebView2Async(null);

            // 测试：初始化时加载一个简单的 HTML
            Preview.NavigateToString("<h1>试卷预览区域</h1><p>请在左侧输入小说...</p>");
        }

        public void ToastMsg(string msg, int duration = 3000)
        {
            // 创建 Border
            Border border = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6B0B0")),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8),
                Margin = new Thickness(8)
            };

            // 创建 StackPanel
            StackPanel stackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 创建 PackIcon
            PackIcon packIcon = new PackIcon
            {
                Kind = PackIconKind.InfoOutline,
                Width = 16,
                Height = 26,
                Foreground = Brushes.AliceBlue
            };

            // 创建 TextBlock
            TextBlock textBlock = new TextBlock
            {
                Text = msg,
                Foreground = Brushes.AliceBlue,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 组装控件
            stackPanel.Children.Add(packIcon);
            stackPanel.Children.Add(textBlock);
            border.Child = stackPanel;

            // 添加到 StackPanel
            Toast.Children.Add(border);

            // 设置定时器，指定时间后删除
            DispatcherTimer timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(duration)
            };

            timer.Tick += (sender, e) =>
            {
                timer.Stop();
                Toast.Children.Remove(border);
            };

            timer.Start();
        }
    }
}