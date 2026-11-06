using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace EasyIP
{
    /// <summary>Loads MainWindow.xaml and drives it. All slow work runs off the UI thread.</summary>
    public class MainView
    {
        class Field
        {
            public TextBox Box;
            public TextBlock Error;
        }

        public readonly Window Window;
        readonly ComboBox cbAdapter, cbProfile;
        readonly TextBlock txtProfileHint, txtMore, txtApply, bannerText, errName;
        readonly TextBox tbProfileName;
        readonly ToggleButton btnMore;
        readonly FrameworkElement panelSaved, panelMore, panelName, busy;
        readonly Button btnApply, btnDhcp, btnSave, btnRefresh, btnDeleteProfile, btnNameOk, btnNameCancel;
        readonly Ellipse dhcpLed;
        readonly Border banner;
        readonly Dictionary<string, Field> fields = new Dictionary<string, Field>();
        readonly DispatcherTimer changeDebounce;

        Settings settings;
        string listSignature = "";
        string editedProfileName;
        bool working, suppressEvents, refreshing, fillingForm;
        bool dirty;   // the form holds something other than the adapter's current values

        public MainView()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(s);

            cbAdapter = Find<ComboBox>("CbAdapter");
            cbProfile = Find<ComboBox>("CbProfile");
            txtProfileHint = Find<TextBlock>("TxtProfileHint");
            txtMore = Find<TextBlock>("TxtMore");
            txtApply = Find<TextBlock>("TxtApply");
            bannerText = Find<TextBlock>("BannerText");
            errName = Find<TextBlock>("ErrName");
            tbProfileName = Find<TextBox>("TbProfileName");
            btnMore = Find<ToggleButton>("BtnMore");
            panelSaved = Find<FrameworkElement>("PanelSaved");
            panelMore = Find<FrameworkElement>("PanelMore");
            panelName = Find<FrameworkElement>("PanelName");
            busy = Find<FrameworkElement>("Busy");
            btnApply = Find<Button>("BtnApply");
            btnDhcp = Find<Button>("BtnDhcp");
            btnSave = Find<Button>("BtnSave");
            btnRefresh = Find<Button>("BtnRefresh");
            btnDeleteProfile = Find<Button>("BtnDeleteProfile");
            btnNameOk = Find<Button>("BtnNameOk");
            btnNameCancel = Find<Button>("BtnNameCancel");
            dhcpLed = Find<Ellipse>("DhcpLed");
            banner = Find<Border>("Banner");

            AddField(IpValidator.FieldIp, "TbIp", "ErrIp");
            AddField(IpValidator.FieldMask, "TbMask", "ErrMask");
            AddField(IpValidator.FieldGateway, "TbGateway", "ErrGateway");
            AddField(IpValidator.FieldDns1, "TbDns1", "ErrDns1");
            AddField(IpValidator.FieldDns2, "TbDns2", "ErrDns2");

            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("EasyIP.ico"))
                Window.Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            settings = Storage.Load();
            RebindProfiles(null);

            cbAdapter.SelectionChanged += OnAdapterChanged;
            cbProfile.SelectionChanged += OnProfileChanged;
            btnApply.Click += OnApply;
            btnDhcp.Click += OnDhcp;
            btnRefresh.Click += (s, e) => { HideBanner(); Refresh(true); };
            btnSave.Click += OnSaveProfile;
            btnDeleteProfile.Click += OnDeleteProfile;
            btnNameOk.Click += (s, e) => ConfirmProfileName();
            btnNameCancel.Click += (s, e) => panelName.Visibility = Visibility.Collapsed;
            btnMore.Checked += (s, e) => ShowMore(true);
            btnMore.Unchecked += (s, e) => ShowMore(false);
            tbProfileName.PreviewKeyDown += OnProfileNameKey;
            fields[IpValidator.FieldIp].Box.LostFocus += OnIpLostFocus;
            fields[IpValidator.FieldMask].Box.LostFocus += OnMaskLostFocus;

            changeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            changeDebounce.Tick += (s, e) => { changeDebounce.Stop(); Refresh(false); };
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;

            Window.SizeChanged += (s, e) => KeepOnScreen();
            Window.Loaded += (s, e) => Refresh(true);
            Window.Closed += (s, e) => NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        }

        /// <summary>The window grows downward when panels open. Keep the buttons reachable on small screens.</summary>
        void KeepOnScreen()
        {
            Rect area = SystemParameters.WorkArea;
            bool onPrimary = Window.Left >= area.Left && Window.Left < area.Right && Window.Top < area.Bottom;
            if (onPrimary && Window.Top + Window.ActualHeight > area.Bottom)
                Window.Top = Math.Max(area.Top, area.Bottom - Window.ActualHeight);
        }

        T Find<T>(string name) where T : class
        {
            return (T)Window.FindName(name);
        }

        void AddField(string key, string box, string error)
        {
            var f = new Field { Box = Find<TextBox>(box), Error = Find<TextBlock>(error) };
            f.Box.TextChanged += (s, e) =>
            {
                SetFieldError(f, null);
                if (!fillingForm) OnFormEdited();
            };
            fields[key] = f;
        }

        void OnNetworkChanged(object sender, EventArgs e)
        {
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                changeDebounce.Stop();
                changeDebounce.Start();
            }));
        }

        AdapterInfo Selected { get { return cbAdapter.SelectedItem as AdapterInfo; } }

        void ShowMore(bool show)
        {
            panelMore.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            txtMore.Text = show ? "Fewer options <<" : "More options >>";
        }

        // ---------- Refresh ----------

        /// <param name="reloadForm">Always rewrites the form. False still follows the adapter unless the user changed the form.</param>
        async void Refresh(bool reloadForm)
        {
            if (working || refreshing) return;
            refreshing = true;
            try
            {
                List<AdapterInfo> list = await Task.Run(() => NetworkService.ListAdapters());
                if (list.Count == 0)
                {
                    cbAdapter.ItemsSource = null;
                    listSignature = "";
                    btnApply.IsEnabled = btnDhcp.IsEnabled = false;
                    ShowBanner(ApplyOutcome.Warning, "No network adapter found.");
                    return;
                }

                if (!cbAdapter.IsDropDownOpen) BindAdapters(list);
                await ShowAdapterState(reloadForm);
            }
            catch (Exception ex)
            {
                Log.Write("refresh failed: " + ex);
                ShowBanner(ApplyOutcome.Failed, "Couldn't read the network adapters. Press refresh to try again.");
            }
            finally
            {
                refreshing = false;
            }
        }

        void BindAdapters(List<AdapterInfo> list)
        {
            string signature = string.Join(";", list.Select(a => a.Signature));
            if (signature == listSignature) return;
            string keep = Selected != null ? Selected.Key : settings.LastAdapter;
            suppressEvents = true;
            cbAdapter.ItemsSource = list;
            cbAdapter.SelectedItem = list.FirstOrDefault(a => a.Key == keep) ?? list[0];
            suppressEvents = false;
            listSignature = signature;
        }

        async Task ShowAdapterState(bool reloadForm)
        {
            AdapterInfo adapter = Selected;
            if (adapter == null) return;
            AdapterState state = await Task.Run(() => NetworkService.ReadState(adapter));
            if (Selected != adapter) return;   // user moved on while reading

            bool disabled = adapter.Status == LinkStatus.Disabled;
            dhcpLed.Fill = (Brush)new BrushConverter().ConvertFromString(
                state.Configured.Dhcp && !disabled ? "#22E06B" : "#6B7079");
            txtApply.Text = disabled ? "Enable" : "Apply";
            btnApply.IsEnabled = !working;
            btnDhcp.IsEnabled = !working && !disabled;

            if (reloadForm || !dirty) FillForm(state);
        }

        void OnAdapterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressEvents || Selected == null) return;
            HideBanner();
            settings.LastAdapter = Selected.Key;
            Storage.Save(settings);
            Guard(() => ShowAdapterState(true));
        }

        async void Guard(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex)
            {
                Log.Write("ui action failed: " + ex);
                ShowBanner(ApplyOutcome.Failed, "Something went wrong reading the adapter. Press refresh.");
            }
        }

        // ---------- Form ----------

        void FillForm(AdapterState state)
        {
            ClearProfileSelection();
            editedProfileName = null;
            string ip = state.ShownIp, mask = state.ShownMask;
            AdapterInfo adapter = Selected;
            if (adapter != null && ip.Length > 0 && mask.Length > 0) RememberAddress(adapter, ip, mask);
            else if (adapter != null)
            {
                // No address right now, for example an unplugged adapter on DHCP. Never show blank boxes.
                Profile known = settings.LastKnown.FirstOrDefault(p => p.Name == adapter.Name);
                ip = known != null ? known.Ip : "0.0.0.0";
                mask = known != null ? known.Mask : "255.255.255.0";
            }
            SetFields(ip, mask, state.ShownGateway,
                state.ShownDns.ElementAtOrDefault(0), state.ShownDns.ElementAtOrDefault(1));
            foreach (Field f in fields.Values) SetFieldError(f, null);
            dirty = false;
        }

        void RememberAddress(AdapterInfo adapter, string ip, string mask)
        {
            Profile known = settings.LastKnown.FirstOrDefault(p => p.Name == adapter.Name);
            if (known != null && known.Ip == ip && known.Mask == mask) return;
            settings.LastKnown.RemoveAll(p => p.Name == adapter.Name);
            settings.LastKnown.Add(new Profile { Name = adapter.Name, Ip = ip, Mask = mask });
            Storage.Save(settings);
        }

        void OnFormEdited()
        {
            dirty = true;
            // An edited form no longer matches its saved entry. Deselecting lets it be picked again.
            var p = cbProfile.SelectedItem as Profile;
            if (p == null) return;
            editedProfileName = p.Name;
            ClearProfileSelection();
        }

        void SetFields(string ip, string mask, string gateway, string dns1, string dns2)
        {
            fillingForm = true;
            fields[IpValidator.FieldIp].Box.Text = ip ?? "";
            fields[IpValidator.FieldMask].Box.Text = mask ?? "";
            fields[IpValidator.FieldGateway].Box.Text = gateway ?? "";
            fields[IpValidator.FieldDns1].Box.Text = dns1 ?? "";
            fields[IpValidator.FieldDns2].Box.Text = dns2 ?? "";
            fillingForm = false;
        }

        string Value(string key) { return fields[key].Box.Text.Trim(); }

        void OnIpLostFocus(object sender, RoutedEventArgs e)
        {
            TextBox ip = fields[IpValidator.FieldIp].Box, mask = fields[IpValidator.FieldMask].Box;
            string address, cidrMask;
            if (IpValidator.TrySplitCidr(ip.Text, out address, out cidrMask))
            {
                ip.Text = address;
                mask.Text = cidrMask;
                return;
            }
            uint parsed;
            if (mask.Text.Trim().Length == 0 && IpValidator.TryParse(ip.Text, out parsed))
                mask.Text = "255.255.255.0";
        }

        void OnMaskLostFocus(object sender, RoutedEventArgs e)
        {
            TextBox mask = fields[IpValidator.FieldMask].Box;
            string normalized;
            if (IpValidator.TryNormalizeMask(mask.Text, out normalized)) mask.Text = normalized;
        }

        void SetFieldError(Field f, string message)
        {
            f.Box.Tag = message == null ? null : "error";
            f.Error.Text = message ?? "";
            f.Error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        bool ValidateForm()
        {
            OnIpLostFocus(null, null);
            OnMaskLostFocus(null, null);
            Dictionary<string, string> errors = IpValidator.Validate(
                Value(IpValidator.FieldIp), Value(IpValidator.FieldMask), Value(IpValidator.FieldGateway),
                Value(IpValidator.FieldDns1), Value(IpValidator.FieldDns2));
            foreach (KeyValuePair<string, Field> f in fields)
            {
                string message;
                errors.TryGetValue(f.Key, out message);
                SetFieldError(f.Value, message);
            }
            if (errors.Count == 0) return true;

            // A problem in a hidden field must never be invisible.
            if (errors.Keys.Any(k => k != IpValidator.FieldIp && k != IpValidator.FieldMask))
                btnMore.IsChecked = true;
            fields.First(f => errors.ContainsKey(f.Key)).Value.Box.Focus();
            return false;
        }

        // ---------- Apply ----------

        async void OnApply(object sender, RoutedEventArgs e)
        {
            AdapterInfo adapter = Selected;
            if (adapter == null || working) return;
            HideBanner();
            try
            {
                if (adapter.Status == LinkStatus.Disabled) await EnableSelected(adapter);
                else if (ValidateForm()) await Change(adapter, ReadForm());
            }
            catch (Exception ex) { OnChangeCrashed(ex); }
        }

        async void OnDhcp(object sender, RoutedEventArgs e)
        {
            AdapterInfo adapter = Selected;
            if (adapter == null || working) return;
            HideBanner();
            try { await Change(adapter, IpConfig.Automatic()); }
            catch (Exception ex) { OnChangeCrashed(ex); }
        }

        void OnChangeCrashed(Exception ex)
        {
            SetWorking(false);
            Log.Write("change crashed: " + ex);
            ShowBanner(ApplyOutcome.Failed, "Something went wrong: " + ex.Message);
        }

        async Task Change(AdapterInfo adapter, IpConfig target)
        {
            SetWorking(true);
            ApplyResult result;
            try { result = await Task.Run(() => NetworkService.Apply(adapter, target)); }
            finally { SetWorking(false); }

            ShowBanner(result.Outcome, result.Message);
            List<AdapterInfo> list = await Task.Run(() => NetworkService.ListAdapters());
            if (list.Count > 0) BindAdapters(list);
            // After a failure, keep what the user typed so it can be corrected.
            await ShowAdapterState(result.Outcome != ApplyOutcome.Failed);
        }

        IpConfig ReadForm()
        {
            var config = new IpConfig
            {
                Dhcp = false,
                Ip = Value(IpValidator.FieldIp),
                Mask = Value(IpValidator.FieldMask),
                Gateway = Value(IpValidator.FieldGateway)
            };
            string d1 = Value(IpValidator.FieldDns1), d2 = Value(IpValidator.FieldDns2);
            if (d1.Length > 0) config.Dns.Add(d1);
            if (d2.Length > 0) config.Dns.Add(d2);
            return config;
        }

        async Task EnableSelected(AdapterInfo adapter)
        {
            SetWorking(true);
            string error = null;
            bool ok;
            try { ok = await Task.Run(() => NetworkService.EnableAdapter(adapter, out error)); }
            finally { SetWorking(false); }
            if (ok) ShowBanner(ApplyOutcome.Success, adapter.Name + " is enabled.");
            else ShowBanner(ApplyOutcome.Failed, "Couldn't enable " + adapter.Name + ": " + error);
            await Task.Delay(1500);
            Refresh(true);
        }

        void SetWorking(bool on)
        {
            working = on;
            busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            btnApply.IsEnabled = !on;
            btnDhcp.IsEnabled = !on;
            btnSave.IsEnabled = !on;
            btnRefresh.IsEnabled = !on;
            cbAdapter.IsEnabled = !on;
            cbProfile.IsEnabled = !on;
            foreach (Field f in fields.Values) f.Box.IsEnabled = !on;
            txtApply.Text = on ? "Applying..." : "Apply";
        }

        void ShowBanner(ApplyOutcome kind, string text)
        {
            string bg, fg, line;
            switch (kind)
            {
                case ApplyOutcome.Success: bg = "#D5F2DF"; fg = "#14532D"; line = "#7CC49A"; break;
                case ApplyOutcome.Warning: bg = "#FBEBC2"; fg = "#78350F"; line = "#D9B25F"; break;
                default: bg = "#F8D7D7"; fg = "#7F1D1D"; line = "#D98888"; break;
            }
            var converter = new BrushConverter();
            banner.Background = (Brush)converter.ConvertFromString(bg);
            banner.BorderBrush = (Brush)converter.ConvertFromString(line);
            bannerText.Foreground = (Brush)converter.ConvertFromString(fg);
            bannerText.Text = text;
            banner.Visibility = Visibility.Visible;
        }

        void HideBanner() { banner.Visibility = Visibility.Collapsed; }

        // ---------- Saved IPs ----------

        void RebindProfiles(string selectName)
        {
            suppressEvents = true;
            List<Profile> sorted = settings.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            cbProfile.ItemsSource = sorted;
            cbProfile.SelectedItem = sorted.FirstOrDefault(p => p.Name == selectName);
            suppressEvents = false;
            panelSaved.Visibility = sorted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateProfileUi();
        }

        void ClearProfileSelection()
        {
            suppressEvents = true;
            cbProfile.SelectedItem = null;
            suppressEvents = false;
            UpdateProfileUi();
        }

        void UpdateProfileUi()
        {
            bool none = cbProfile.SelectedItem == null;
            txtProfileHint.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            btnDeleteProfile.IsEnabled = !none;
        }

        void OnProfileChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateProfileUi();
            if (suppressEvents) return;
            var p = cbProfile.SelectedItem as Profile;
            if (p == null) return;
            HideBanner();
            SetFields(p.Ip, p.Mask, p.Gateway, p.Dns1, p.Dns2);
            dirty = true;
        }

        void OnSaveProfile(object sender, RoutedEventArgs e)
        {
            HideBanner();
            if (!ValidateForm()) return;
            var current = cbProfile.SelectedItem as Profile;
            tbProfileName.Text = current != null ? current.Name : editedProfileName ?? "";
            errName.Visibility = Visibility.Collapsed;
            panelName.Visibility = Visibility.Visible;
            tbProfileName.Focus();
            tbProfileName.SelectAll();
        }

        void OnProfileNameKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; ConfirmProfileName(); }
            else if (e.Key == Key.Escape) { e.Handled = true; panelName.Visibility = Visibility.Collapsed; }
        }

        void ConfirmProfileName()
        {
            string name = tbProfileName.Text.Trim();
            if (name.Length == 0)
            {
                errName.Text = "Give it a name";
                errName.Visibility = Visibility.Visible;
                return;
            }
            settings.Profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            settings.Profiles.Add(new Profile
            {
                Name = name,
                Ip = Value(IpValidator.FieldIp),
                Mask = Value(IpValidator.FieldMask),
                Gateway = Value(IpValidator.FieldGateway),
                Dns1 = Value(IpValidator.FieldDns1),
                Dns2 = Value(IpValidator.FieldDns2)
            });
            bool saved = Storage.Save(settings);
            panelName.Visibility = Visibility.Collapsed;
            RebindProfiles(name);
            if (saved) ShowBanner(ApplyOutcome.Success, "\"" + name + "\" saved.");
            else ShowBanner(ApplyOutcome.Warning, "It works for now, but it couldn't be saved to disk.");
        }

        void OnDeleteProfile(object sender, RoutedEventArgs e)
        {
            var p = cbProfile.SelectedItem as Profile;
            if (p == null) return;
            settings.Profiles.RemoveAll(x => x.Name == p.Name);
            Storage.Save(settings);
            RebindProfiles(null);
            ShowBanner(ApplyOutcome.Success, "\"" + p.Name + "\" deleted.");
        }
    }
}
