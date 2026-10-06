using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace VerticalPlayer.Dashcam
{
    /// <summary>情報一覧「イベント」タブの1行が表すイベントの種類。</summary>
    public enum DashcamEventKind
    {
        TunnelEnter,
        TunnelExit,
        SaPa,
        HardBrake,
        HardAccel,
        Steer,
        Impact,
    }

    /// <summary>種別フィルターの選択肢。</summary>
    public enum DashcamEventFilter
    {
        All,
        Tunnel,
        SaPa,
        /// <summary>前後Gの急ブレーキ・急加速。</summary>
        AccelBrake,
        /// <summary>左右G（急ハンドル）・上下G（衝撃）。</summary>
        Impact,
    }

    public sealed record EventFilterOption(DashcamEventFilter Filter, string Text)
    {
        public override string ToString() => Text;
    }

    public sealed record PrerollOption(int Seconds, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>
    /// イベント一覧の1行（読み取り専用）。DataGridの各カラムにそのままバインドする。
    /// </summary>
    public sealed class EventItemViewModel
    {
        public EventItemViewModel(string groupKey, string filePath, DateTime? timestamp, double offsetSeconds,
            DashcamEventKind kind, string detailText)
        {
            GroupKey = groupKey;
            FilePath = filePath;
            int sep = Math.Max(filePath.LastIndexOf('\\'), filePath.LastIndexOf('/'));
            FileName = sep >= 0 ? filePath.Substring(sep + 1) : filePath;
            Timestamp = timestamp;
            OffsetSeconds = Math.Max(0.0, offsetSeconds);
            Kind = kind;
            DetailText = detailText;
        }

        /// <summary>再生側で対象ファイルを特定するキー（DashcamMediaGroup.TimestampKey）。</summary>
        public string GroupKey { get; }

        public string FilePath { get; }

        /// <summary>日時列の元データ（走行実時刻。取得できなければnull）。</summary>
        public DateTime? Timestamp { get; }

        /// <summary>該当ファイル内での発生位置(秒)。</summary>
        public double OffsetSeconds { get; }

        public DashcamEventKind Kind { get; }

        /// <summary>この行が、再生で実際に開いたファイルの精密な解析結果（動画長・地図情報込み）に
        /// 由来するか。falseは、ファイルを開かずにNMEAだけから出した概算。</summary>
        public bool IsPrecise { get; init; }

        // ── カラム ──
        public string DateTimeText => Timestamp?.ToString("yyyy/MM/dd HH:mm:ss") ?? "-";

        public string KindText => Kind switch
        {
            DashcamEventKind.TunnelEnter => "トンネル進入",
            DashcamEventKind.TunnelExit => "トンネル脱出",
            DashcamEventKind.SaPa => "SA/PA付近",
            DashcamEventKind.HardBrake => "急減速",
            DashcamEventKind.HardAccel => "急加速",
            DashcamEventKind.Steer => "急ハンドル",
            DashcamEventKind.Impact => "衝撃検知",
            _ => string.Empty
        };

        public string DetailText { get; }

        public string FileName { get; }

        /// <summary>相対時間列（ファイル内の発生タイムコード。例: 01:23 / 1:02:03）。</summary>
        public string RelativeTimeText
        {
            get
            {
                var t = TimeSpan.FromSeconds(Math.Floor(OffsetSeconds));
                return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
            }
        }

        /// <summary>種別フィルターでの分類。</summary>
        public DashcamEventFilter FilterGroup => Kind switch
        {
            DashcamEventKind.TunnelEnter or DashcamEventKind.TunnelExit => DashcamEventFilter.Tunnel,
            DashcamEventKind.SaPa => DashcamEventFilter.SaPa,
            DashcamEventKind.HardBrake or DashcamEventKind.HardAccel => DashcamEventFilter.AccelBrake,
            _ => DashcamEventFilter.Impact
        };
    }

    /// <summary>引数付き/なし両対応の最小限のICommand（WPFのCommandManagerに依存しない）。</summary>
    public sealed class DashcamRelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public DashcamRelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 情報一覧ウィンドウの「イベント」タブのViewModel。UIスレッドでのみ操作する。
    /// DataGridは<see cref="Events"/>(フィルター適用後)にバインドし、行の選択（ダブルクリック/Enter）は
    /// <see cref="SelectEventCommand"/>で受ける。ジャンプの実処理は<see cref="JumpHandler"/>経由で再生側が行う。
    /// </summary>
    public sealed class DashcamEventListViewModel : INotifyPropertyChanged
    {
        /// <summary>1ファイル分の解析結果（<see cref="UpdateFiles"/>の入力）。</summary>
        public sealed record FileEvents(string GroupKey, IReadOnlyList<EventItemViewModel> Items, bool Precise);

        private readonly List<EventItemViewModel> _all = new();
        private readonly HashSet<string> _preciseGroups = new();

        private EventFilterOption _selectedFilter;
        private PrerollOption _selectedPreroll;
        private string _keyword = string.Empty;
        private EventItemViewModel? _selectedEvent;
        private bool _isIndexing;
        private int _indexDone, _indexTotal;
        private string _message = string.Empty;
        private Func<EventItemViewModel, double, Task<bool>>? _jumpHandler;
        private bool _jumping;
        private EventItemViewModel? _pendingJump;

        public DashcamEventListViewModel()
        {
            FilterOptions = new[]
            {
                new EventFilterOption(DashcamEventFilter.All, "すべて表示"),
                new EventFilterOption(DashcamEventFilter.Tunnel, "トンネル"),
                new EventFilterOption(DashcamEventFilter.SaPa, "SA/PA"),
                new EventFilterOption(DashcamEventFilter.AccelBrake, "急加減速"),
                new EventFilterOption(DashcamEventFilter.Impact, "衝撃検知"),
            };
            PrerollOptions = new[]
            {
                new PrerollOption(0, "0秒前"),
                new PrerollOption(3, "3秒前"),
                new PrerollOption(5, "5秒前"),
                new PrerollOption(10, "10秒前"),
            };
            _selectedFilter = FilterOptions[0];
            _selectedPreroll = PrerollOptions[1]; // 既定は3秒前

            SelectEventCommand = new DashcamRelayCommand(
                p => _ = JumpAsync(p as EventItemViewModel ?? SelectedEvent),
                p => (p as EventItemViewModel ?? SelectedEvent) != null && JumpHandler != null);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>DataGrid.ItemsSourceにバインドする、フィルター適用後の一覧（日時順）。</summary>
        public ObservableCollection<EventItemViewModel> Events { get; } = new();

        public IReadOnlyList<EventFilterOption> FilterOptions { get; }

        public IReadOnlyList<PrerollOption> PrerollOptions { get; }

        public EventFilterOption SelectedFilter
        {
            get => _selectedFilter;
            set
            {
                if (value == null || Equals(_selectedFilter, value)) return;
                _selectedFilter = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        public PrerollOption SelectedPreroll
        {
            get => _selectedPreroll;
            set
            {
                if (value == null || Equals(_selectedPreroll, value)) return;
                _selectedPreroll = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PrerollSeconds));
            }
        }

        /// <summary>ジャンプ時にイベント発生地点の何秒手前から再生するか。</summary>
        public double PrerollSeconds => _selectedPreroll.Seconds;

        /// <summary>イベント名・ファイル名などでの絞り込み（部分一致・大文字小文字無視）。</summary>
        public string Keyword
        {
            get => _keyword;
            set
            {
                value ??= string.Empty;
                if (_keyword == value) return;
                _keyword = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        public EventItemViewModel? SelectedEvent
        {
            get => _selectedEvent;
            set
            {
                if (ReferenceEquals(_selectedEvent, value)) return;
                _selectedEvent = value;
                OnPropertyChanged();
                (SelectEventCommand as DashcamRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>行の選択（ダブルクリック/Enter）で実行する。CommandParameterに行(EventItemViewModel)、
        /// 無ければSelectedEventを使う。</summary>
        public ICommand SelectEventCommand { get; }

        /// <summary>実際にファイルを開いてシークする処理（再生側が設定）。引数は(行, プレロール秒)、戻り値は成否。</summary>
        public Func<EventItemViewModel, double, Task<bool>>? JumpHandler
        {
            get => _jumpHandler;
            set
            {
                _jumpHandler = value;
                (SelectEventCommand as DashcamRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool IsIndexing
        {
            get => _isIndexing;
            private set
            {
                if (_isIndexing == value) return;
                _isIndexing = value;
                OnPropertyChanged();
                RefreshStatus();
            }
        }

        /// <summary>タブ下部などに出す状態表示（件数・解析進捗・失敗メッセージ）。</summary>
        public string StatusText
        {
            get
            {
                string s = $"{Events.Count}件表示 / 全{_all.Count}件";
                if (_isIndexing) s += $"（解析中 {_indexDone}/{_indexTotal}）";
                if (_message.Length > 0) s += $"　{_message}";
                return s;
            }
        }

        /// <summary>ファイル単位で解析結果を差し替える。精密な結果(Precise=true)で一度置き換えたファイルは、
        /// 後から来る概算では上書きしない。まとめて渡せば一覧の再構築は1回で済む。</summary>
        public void UpdateFiles(IReadOnlyList<FileEvents> files)
        {
            if (files == null || files.Count == 0) return;
            bool changed = false;
            foreach (var f in files)
            {
                if (!f.Precise && _preciseGroups.Contains(f.GroupKey)) continue;
                _all.RemoveAll(x => x.GroupKey == f.GroupKey);
                _all.AddRange(f.Items);
                if (f.Precise) _preciseGroups.Add(f.GroupKey);
                changed = true;
            }
            if (!changed) return;
            _all.Sort(CompareItems);
            ApplyFilter();
        }

        public void UpdateFile(string groupKey, IReadOnlyList<EventItemViewModel> items, bool precise)
            => UpdateFiles(new[] { new FileEvents(groupKey, items, precise) });

        /// <summary>ドライブ/フォルダを切り替えた時など、全イベントを消す。</summary>
        public void Clear()
        {
            _all.Clear();
            _preciseGroups.Clear();
            _message = string.Empty;
            SelectedEvent = null;
            ApplyFilter();
        }

        /// <summary>解析の進捗を表示へ反映する（total==0で解析終了）。</summary>
        public void SetIndexProgress(int done, int total)
        {
            _indexDone = done;
            _indexTotal = total;
            IsIndexing = total > 0 && done < total;
            RefreshStatus();
        }

        private static int CompareItems(EventItemViewModel a, EventItemViewModel b)
        {
            if (a.Timestamp.HasValue && b.Timestamp.HasValue)
            {
                int c = a.Timestamp.Value.CompareTo(b.Timestamp.Value);
                if (c != 0) return c;
            }
            else if (a.Timestamp.HasValue != b.Timestamp.HasValue)
            {
                return a.Timestamp.HasValue ? -1 : 1; // 日時不明は末尾
            }
            int f = string.CompareOrdinal(a.FilePath, b.FilePath);
            return f != 0 ? f : a.OffsetSeconds.CompareTo(b.OffsetSeconds);
        }

        private bool Matches(EventItemViewModel e)
        {
            if (_selectedFilter.Filter != DashcamEventFilter.All && e.FilterGroup != _selectedFilter.Filter)
                return false;
            if (_keyword.Length == 0) return true;
            return Contains(e.DetailText, _keyword) || Contains(e.KindText, _keyword)
                || Contains(e.FileName, _keyword) || Contains(e.DateTimeText, _keyword);
        }

        private static bool Contains(string text, string keyword)
            => text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

        private void ApplyFilter()
        {
            var visible = _all.Where(Matches).ToList();

            // 変化が無ければ作り直さない（解析の進行中に選択やスクロールが乱れないように）
            if (visible.Count == Events.Count)
            {
                bool same = true;
                for (int i = 0; i < visible.Count; i++)
                {
                    if (!ReferenceEquals(visible[i], Events[i])) { same = false; break; }
                }
                if (same) { RefreshStatus(); return; }
            }

            var keep = _selectedEvent;
            Events.Clear();
            foreach (var e in visible) Events.Add(e);
            if (keep != null && visible.Contains(keep)) SelectedEvent = keep;
            else if (keep != null) SelectedEvent = null;
            RefreshStatus();
        }

        /// <summary>最後に押された行へジャンプする。実行中に別の行が押された場合は、直前のジャンプ完了後に
        /// 最後の1件だけを実行する（連打で古い位置へ戻らないように）。例外は握りつぶさず状態表示へ出す。</summary>
        private async Task JumpAsync(EventItemViewModel? item)
        {
            if (item == null) return;
            var handler = JumpHandler;
            if (handler == null)
            {
                SetMessage("再生画面と接続されていないためジャンプできません");
                return;
            }

            _pendingJump = item;
            if (_jumping) return;
            _jumping = true;
            try
            {
                while (_pendingJump is { } target)
                {
                    _pendingJump = null;
                    try
                    {
                        bool ok = await handler(target, PrerollSeconds);
                        SetMessage(ok ? string.Empty : $"ジャンプできませんでした: {target.FileName}");
                    }
                    catch (Exception ex)
                    {
                        SetMessage($"ジャンプに失敗しました: {ex.Message}");
                    }
                }
            }
            finally
            {
                _jumping = false;
            }
        }

        private void SetMessage(string message)
        {
            _message = message;
            RefreshStatus();
        }

        private void RefreshStatus() => OnPropertyChanged(nameof(StatusText));

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
