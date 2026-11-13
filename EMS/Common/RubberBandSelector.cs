using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
// 项目同时启用 WinForms，显式锁定 WPF 类型
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace EMS.Common
{
    /// <summary>
    /// 为 ListView（详情/图标视图）附加资源管理器式橡皮筋框选：
    /// 在空白区域按下并拖出矩形，与行矩形相交的条目进入选中集（按住 Ctrl 为追加选中）。
    /// 关键设计：按下时不捕获鼠标、不拦截事件——只有真正拖过阈值才开始框选并临时捕获，
    /// 避免捕获残留把后续所有单击吞掉（曾导致条目点击无法选中）；
    /// 捕获中途被弹出层（ToolTip/菜单）抢走时自动取消框选。
    /// </summary>
    public class RubberBandSelector
    {
        private readonly ListView _list;
        private RubberBandAdorner? _adorner;
        private Point _start;
        private bool _pending;    // 按下点位于空白区，可能成为框选起点
        private bool _dragging;   // 已超过阈值，正在框选
        private HashSet<int> _baseline = [];

        /// <summary>把框选行为附加到目标 ListView（常驻，无需解除）</summary>
        public static void Attach(ListView list) => new RubberBandSelector(list);

        private RubberBandSelector(ListView list)
        {
            _list = list;
            list.PreviewMouseLeftButtonDown += OnMouseDown;
            list.PreviewMouseMove += OnMouseMove;
            list.PreviewMouseLeftButtonUp += OnMouseUp;
            // 安全网：拖动中被 ToolTip 弹出层等抢走捕获时，结束框选状态，不留残影
            list.LostMouseCapture += (_, e) =>
            {
                if (_dragging && e.MouseDevice.Captured != _list) CancelDrag();
            };
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject src) return;

            // 条目、表头、滚动条上的按下完全交还原生逻辑（单击选中/Ctrl/Shift 多选）
            if (FindAncestor<ListViewItem>(src) != null ||
                FindAncestor<GridViewHeaderRowPresenter>(src) != null ||
                FindAncestor<ScrollBar>(src) != null)
            {
                _pending = false;
                return;
            }

            // 空白处按下：仅记录候选起点，不捕获、不拦截
            _start = e.GetPosition(_list);
            _pending = true;
            _dragging = false;
            _baseline = SelectedIndices();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_pending) return;

            var pos = e.GetPosition(_list);
            if (!_dragging)
            {
                // 阈值取系统最小拖动距离与 10px 的较大者，抑制单击手抖误触发
                var threshold = Math.Max(10.0, Math.Max(SystemParameters.MinimumHorizontalDragDistance,
                                                        SystemParameters.MinimumVerticalDragDistance));
                if ((pos - _start).Length < threshold) return;

                _dragging = true;
                if (!_list.CaptureMouse())
                {
                    // 捕获失败（他处持有捕获），放弃本次框选
                    _pending = false;
                    return;
                }

                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(_list);
                if (layer != null)
                {
                    _adorner = new RubberBandAdorner(_list);
                    layer.Add(_adorner);
                }
            }

            e.Handled = true;
            UpdateBand(pos);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_pending) return;
            _pending = false;

            if (_dragging)
            {
                e.Handled = true; // 拦截抬起，选中集以框选结果为准
                _dragging = false; // 先清状态，防止 ReleaseMouseCapture 触发 LostMouseCapture 重入
                if (_list.IsMouseCaptured) _list.ReleaseMouseCapture();
                RemoveAdorner();
                _list.Focus();
                return;
            }

            // 空白单击（未形成拖拽）：与资源管理器一致，清空选中；
            // 不拦截本次事件，原生流程与双击计数均不受影响
            foreach (var i in SelectedIndices())
            {
                if (_list.ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem c)
                    c.IsSelected = false;
            }
        }

        private void CancelDrag()
        {
            _pending = false;
            _dragging = false;
            if (_list.IsMouseCaptured) _list.ReleaseMouseCapture();
            RemoveAdorner();
        }

        private void RemoveAdorner()
        {
            if (_adorner == null) return;
            System.Windows.Documents.AdornerLayer.GetAdornerLayer(_list)?.Remove(_adorner);
            _adorner = null;
        }

        private void UpdateBand(Point current)
        {
            var band = new Rect(_start, current);
            band.X = Math.Max(0, band.X);
            band.Y = Math.Max(0, band.Y);
            band.Intersect(new Rect(0, 0, _list.ActualWidth, _list.ActualHeight));

            // 表头区域不可框选，矩形与命中测试都限制在表头以下
            var headerBottom = HeaderBottom();
            if (band.Y < headerBottom)
            {
                band.Y = headerBottom;
                band.Height = Math.Max(0, band.Bottom - headerBottom);
            }

            _adorner?.SetBand(band);

            var append = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var hits = new HashSet<int>();
            for (var i = 0; i < _list.Items.Count; i++)
            {
                if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not ListViewItem container) continue;

                var bounds = container.TransformToVisual(_list)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                bounds.Intersect(new Rect(0, headerBottom, _list.ActualWidth, _list.ActualHeight - headerBottom));
                if (bounds.Width > 0 && bounds.Height > 0 && bounds.IntersectsWith(band))
                    hits.Add(i);
            }

            var target = append ? new HashSet<int>(_baseline) : [];
            target.UnionWith(hits);

            for (var i = 0; i < _list.Items.Count; i++)
            {
                if (_list.ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem container)
                    container.IsSelected = target.Contains(i);
            }
        }

        private double HeaderBottom()
        {
            var header = FindVisualChild<GridViewHeaderRowPresenter>(_list);
            if (header == null) return 0;
            return header.TransformToVisual(_list).TransformBounds(
                new Rect(0, 0, header.ActualWidth, header.ActualHeight)).Bottom;
        }

        private HashSet<int> SelectedIndices()
        {
            var result = new HashSet<int>();
            for (var i = 0; i < _list.Items.Count; i++)
            {
                if (_list.ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem { IsSelected: true })
                    result.Add(i);
            }
            return result;
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null && current is not T)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            return current as T;
        }

        private static T? FindVisualChild<T>(DependencyObject current) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                if (child is T found) return found;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>框选矩形（半透明蓝，资源管理器样式）</summary>
        private sealed class RubberBandAdorner : Adorner
        {
            private readonly Rectangle _rect;
            private Rect _band;

            public RubberBandAdorner(UIElement adorned) : base(adorned)
            {
                IsHitTestVisible = false;
                _rect = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(0x33, 0, 120, 215)),
                    Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0, 120, 215)),
                    StrokeThickness = 1,
                };
                AddVisualChild(_rect);
            }

            public void SetBand(Rect band)
            {
                _band = band;
                InvalidateArrange();
            }

            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => _rect;

            protected override Size MeasureOverride(Size constraint) => constraint;

            protected override Size ArrangeOverride(Size finalSize)
            {
                _rect.Arrange(_band);
                return finalSize;
            }
        }
    }
}
