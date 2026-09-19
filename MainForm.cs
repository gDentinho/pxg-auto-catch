using System.ComponentModel;
using System.Text;
using System.Windows.Forms;

namespace PxGCorpseReader;

public sealed class MainForm : Form
{
    private readonly ProcessMemoryReader _reader = new();
    private readonly BridgeClient _bridgeClient = new();
    private readonly BridgeLoader _bridgeLoader = new();

    private CatchMonitor? _monitor;
    private CancellationTokenSource? _monitorCts;

    private readonly BindingList<CatchRule> _rules = new();
    private readonly BindingList<BallCatalogEntry> _balls = new();

    private readonly SemaphoreSlim _ballActionGate = new(1, 1);
    private readonly Dictionary<uint, DateTime> _recentCatchAttempts = new();

    private readonly UserSettings _settings;

    // Main tab
    private readonly Button _connect = new();
    private readonly Label _mainStatus = new();
    private readonly CheckBox _continuousAutoCatch = new();
    private readonly NumericUpDown _internalBallDelayMs = new();

    private readonly TextBox _rulePokemon = new();
    private readonly ComboBox _ruleBall = new();
    private readonly Button _addRule = new();
    private readonly DataGridView _rulesGrid = new();
    private readonly Label _rulesStatus = new();

    // Developer tab - state
    private readonly Label _status = new();
    private readonly Label _build = new();
    private readonly Label _base = new();
    private readonly Label _map = new();
    private readonly Label _game = new();
    private readonly Label _gameIds = new();
    private readonly Label _player = new();
    private readonly Label _targetLabel = new();
    private readonly Label _targetHp = new();
    private readonly Label _targetPos = new();
    private readonly Label _resolver = new();
    private readonly Label _bridgeStatus = new();

    // Developer tab - controls
    private readonly Button _devAttach = new();
    private readonly Button _devLoadBridge = new();
    private readonly Button _devStart = new();
    private readonly Button _devStop = new();
    private readonly CheckBox _probeOnCorpse = new();
    private readonly NumericUpDown _pollMs = new();

    // Developer tab - ball IDs
    private readonly DataGridView _ballCatalogGrid = new();
    private readonly Button _saveBallCatalog = new();
    private readonly Label _ballCatalogStatus = new();

    // Developer tab - logs
    private readonly TextBox _log = new();
    private readonly Button _clearLog = new();

    private bool _bridgeReady;
    private bool _connectBusy;

    public MainForm()
    {
        Text = "PxG Auto Catch v0.10.0";
        Width = 940;
        Height = 720;
        MinimumSize = new Size(780, 600);
        StartPosition = FormStartPosition.CenterScreen;

        _settings = UserSettingsStore.Load();

        var loadedRules = CatchRuleStore.Load();

        foreach (var rule in loadedRules)
            _rules.Add(rule);

        foreach (var ball in BallCatalogStore.Load(loadedRules))
            _balls.Add(ball);

        NormalizeRuleBallMappings();
        BuildUi();
        BindData();
        ApplySavedSettings();

        FormClosing += (_, _) => Shutdown();
    }

    private void BuildUi()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };

        var mainTab = new TabPage("Auto Catch");
        var devTab = new TabPage("Modo desenvolvedor");

        tabs.TabPages.Add(mainTab);
        tabs.TabPages.Add(devTab);
        Controls.Add(tabs);

        BuildMainTab(mainTab);
        BuildDeveloperTab(devTab);
    }

    private void BuildMainTab(TabPage page)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        page.Controls.Add(root);

        var connectionBox = new GroupBox
        {
            Text = "Conexão",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10)
        };

        var connectionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        _connect.Text = "Conectar ao PxG";
        _connect.AutoSize = true;
        _connect.Click += async (_, _) =>
            await ConnectEverythingAsync();

        _mainStatus.Text = "Desconectado";
        _mainStatus.AutoSize = true;
        _mainStatus.Padding = new Padding(12, 8, 0, 0);

        connectionPanel.Controls.Add(_connect);
        connectionPanel.Controls.Add(_mainStatus);
        connectionBox.Controls.Add(connectionPanel);
        root.Controls.Add(connectionBox);

        var autoBox = new GroupBox
        {
            Text = "Auto Catch",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10)
        };

        var autoPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        _continuousAutoCatch.Text = "Ativar Auto Catch contínuo";
        _continuousAutoCatch.AutoSize = true;
        _continuousAutoCatch.CheckedChanged += (_, _) =>
        {
            SaveUserSettings();

            Log(
                $"AUTO_CATCH_CONTINUOUS enabled=" +
                $"{(_continuousAutoCatch.Checked ? 1 : 0)}; " +
                $"rules={_rules.Count}");
        };

        _internalBallDelayMs.Minimum = 0;
        _internalBallDelayMs.Maximum = 5000;
        _internalBallDelayMs.Increment = 50;
        _internalBallDelayMs.Width = 85;
        _internalBallDelayMs.ValueChanged += (_, _) =>
            SaveUserSettings();

        autoPanel.Controls.Add(_continuousAutoCatch);

        autoPanel.Controls.Add(new Label
        {
            Text = "Delay após encontrar o corpse:",
            AutoSize = true,
            Padding = new Padding(18, 8, 0, 0)
        });

        autoPanel.Controls.Add(_internalBallDelayMs);

        autoPanel.Controls.Add(new Label
        {
            Text = "ms",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        });

        autoBox.Controls.Add(autoPanel);
        root.Controls.Add(autoBox);

        var addBox = new GroupBox
        {
            Text = "Adicionar Pokémon para captura",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10)
        };

        var addPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        _rulePokemon.Width = 180;
        _rulePokemon.PlaceholderText = "Nome do Pokémon";
        _rulePokemon.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddOrUpdateRuleFromMain();
            }
        };

        _ruleBall.Width = 180;
        _ruleBall.DropDownStyle =
            ComboBoxStyle.DropDownList;

        _addRule.Text = "Adicionar";
        _addRule.AutoSize = true;
        _addRule.Click += (_, _) =>
            AddOrUpdateRuleFromMain();

        addPanel.Controls.Add(new Label
        {
            Text = "Pokémon:",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        });
        addPanel.Controls.Add(_rulePokemon);

        addPanel.Controls.Add(new Label
        {
            Text = "Ball:",
            AutoSize = true,
            Padding = new Padding(12, 8, 0, 0)
        });
        addPanel.Controls.Add(_ruleBall);
        addPanel.Controls.Add(_addRule);

        addBox.Controls.Add(addPanel);
        root.Controls.Add(addBox);

        var listBox = new GroupBox
        {
            Text = "Pokémon configurados",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };

        var listLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2
        };

        listLayout.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        listLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        _rulesStatus.AutoSize = true;
        _rulesStatus.Padding =
            new Padding(0, 2, 0, 8);

        ConfigureRulesGrid();

        listLayout.Controls.Add(_rulesStatus);
        listLayout.Controls.Add(_rulesGrid);

        listBox.Controls.Add(listLayout);
        root.Controls.Add(listBox);
    }

    private void BuildDeveloperTab(TabPage page)
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };

        var stateTab = new TabPage("Estado / Bridge");
        var ballTab = new TabPage("Poké Balls / Item IDs");
        var logsTab = new TabPage("Logs");

        tabs.TabPages.Add(stateTab);
        tabs.TabPages.Add(ballTab);
        tabs.TabPages.Add(logsTab);

        page.Controls.Add(tabs);

        BuildDeveloperStateTab(stateTab);
        BuildDeveloperBallTab(ballTab);
        BuildDeveloperLogsTab(logsTab);
    }

    private void BuildDeveloperStateTab(TabPage page)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        page.Controls.Add(root);

        var controls = new GroupBox
        {
            Text = "Controles manuais",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8)
        };

        var controlPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        _devAttach.Text = "Anexar Reader";
        _devAttach.AutoSize = true;
        _devAttach.Click += (_, _) =>
            AttachReaderOnly(showErrors: true);

        _devLoadBridge.Text = "Bridge + PING";
        _devLoadBridge.AutoSize = true;
        _devLoadBridge.Click += async (_, _) =>
            await LoadBridgeAsync(showErrors: true);

        _devStart.Text = "Iniciar monitor";
        _devStart.AutoSize = true;
        _devStart.Click += (_, _) =>
            StartMonitor();

        _devStop.Text = "Parar monitor";
        _devStop.AutoSize = true;
        _devStop.Click += (_, _) =>
            StopMonitor(updateMainStatus: true);

        _probeOnCorpse.Text = "Executar diagnóstico da bridge no corpse";
        _probeOnCorpse.AutoSize = true;

        _pollMs.Minimum = 30;
        _pollMs.Maximum = 1000;
        _pollMs.Value = 60;
        _pollMs.Width = 75;

        controlPanel.Controls.Add(_devAttach);
        controlPanel.Controls.Add(_devLoadBridge);
        controlPanel.Controls.Add(_devStart);
        controlPanel.Controls.Add(_devStop);

        controlPanel.Controls.Add(new Label
        {
            Text = "Poll:",
            AutoSize = true,
            Padding = new Padding(12, 8, 0, 0)
        });
        controlPanel.Controls.Add(_pollMs);

        controlPanel.Controls.Add(new Label
        {
            Text = "ms",
            AutoSize = true,
            Padding = new Padding(0, 8, 10, 0)
        });

        controlPanel.Controls.Add(_probeOnCorpse);
        controls.Controls.Add(controlPanel);
        root.Controls.Add(controls);

        var bridgeLine = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true
        };

        _status.AutoSize = true;
        _status.Text = "Reader: desconectado";

        _bridgeStatus.AutoSize = true;
        _bridgeStatus.Text = "Bridge: não carregada";
        _bridgeStatus.Padding =
            new Padding(18, 0, 0, 0);

        bridgeLine.Controls.Add(_status);
        bridgeLine.Controls.Add(_bridgeStatus);
        root.Controls.Add(bridgeLine);

        var stateBox = new GroupBox
        {
            Text = "Estado lido diretamente do pxgme.exe",
            Dock = DockStyle.Fill
        };

        var state = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(8)
        };

        AddRow(state, "Build:", _build);
        AddRow(state, "Module Base:", _base);
        AddRow(state, "Game:", _game);
        AddRow(state, "Map*:", _map);
        AddRow(state, "Game IDs A/F/C:", _gameIds);
        AddRow(state, "LocalPlayer posição:", _player);
        AddRow(state, "Target:", _targetLabel);
        AddRow(state, "Target HP:", _targetHp);
        AddRow(state, "Target posição:", _targetPos);
        AddRow(state, "Resolver:", _resolver);

        stateBox.Controls.Add(state);
        root.Controls.Add(stateBox);
    }

    private void BuildDeveloperBallTab(TabPage page)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            Padding = new Padding(12)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        page.Controls.Add(root);

        var info = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Text =
                "Relação interna Nome da Ball → Item ID. " +
                "Somente Balls com Item ID maior que zero aparecem na lista " +
                "da tela principal. Ultra Ball = 2652 já está validada. " +
                "IDs não confirmados permanecem 0 até serem configurados."
        };

        root.Controls.Add(info);

        ConfigureBallCatalogGrid();
        root.Controls.Add(_ballCatalogGrid);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true
        };

        _saveBallCatalog.Text = "Salvar catálogo";
        _saveBallCatalog.AutoSize = true;
        _saveBallCatalog.Click += (_, _) =>
            SaveBallCatalog(showLog: true);

        _ballCatalogStatus.AutoSize = true;
        _ballCatalogStatus.Padding =
            new Padding(12, 8, 0, 0);

        footer.Controls.Add(_saveBallCatalog);
        footer.Controls.Add(_ballCatalogStatus);

        root.Controls.Add(footer);
    }

    private void BuildDeveloperLogsTab(TabPage page)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            Padding = new Padding(8)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        page.Controls.Add(root);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true
        };

        _clearLog.Text = "Limpar logs";
        _clearLog.AutoSize = true;
        _clearLog.Click += (_, _) =>
            _log.Clear();

        top.Controls.Add(_clearLog);

        top.Controls.Add(new Label
        {
            Text =
                "Os logs detalhados ficam apenas aqui. " +
                "O arquivo diário continua sendo salvo na pasta logs.",
            AutoSize = true,
            Padding = new Padding(12, 7, 0, 0)
        });

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.Dock = DockStyle.Fill;
        _log.Font = new Font("Consolas", 9);

        root.Controls.Add(top);
        root.Controls.Add(_log);
    }

    private void ConfigureRulesGrid()
    {
        _rulesGrid.Dock = DockStyle.Fill;
        _rulesGrid.AutoGenerateColumns = false;
        _rulesGrid.AllowUserToAddRows = false;
        _rulesGrid.AllowUserToDeleteRows = false;
        _rulesGrid.AllowUserToResizeRows = false;
        _rulesGrid.ReadOnly = true;
        _rulesGrid.MultiSelect = false;
        _rulesGrid.SelectionMode =
            DataGridViewSelectionMode.FullRowSelect;
        _rulesGrid.RowHeadersVisible = false;
        _rulesGrid.AutoSizeRowsMode =
            DataGridViewAutoSizeRowsMode.AllCells;

        _rulesGrid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                HeaderText = "Pokémon",
                DataPropertyName =
                    nameof(CatchRule.Pokemon),
                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 55
            });

        _rulesGrid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                HeaderText = "Ball",
                DataPropertyName =
                    nameof(CatchRule.BallName),
                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 35
            });

        _rulesGrid.Columns.Add(
            new DataGridViewButtonColumn
            {
                HeaderText = "",
                Text = "Remover",
                UseColumnTextForButtonValue = true,
                Width = 90
            });

        _rulesGrid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 ||
                e.ColumnIndex != 2)
            {
                return;
            }

            if (_rulesGrid.Rows[e.RowIndex]
                    .DataBoundItem
                is CatchRule rule)
            {
                RemoveRule(rule);
            }
        };
    }

    private void ConfigureBallCatalogGrid()
    {
        _ballCatalogGrid.Dock = DockStyle.Fill;
        _ballCatalogGrid.AutoGenerateColumns = false;
        _ballCatalogGrid.AllowUserToAddRows = false;
        _ballCatalogGrid.AllowUserToDeleteRows = false;
        _ballCatalogGrid.RowHeadersVisible = false;
        _ballCatalogGrid.SelectionMode =
            DataGridViewSelectionMode.FullRowSelect;

        _ballCatalogGrid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                HeaderText = "Poké Ball",
                DataPropertyName =
                    nameof(BallCatalogEntry.Name),
                ReadOnly = true,
                AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.Fill
            });

        _ballCatalogGrid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                HeaderText = "Item ID",
                DataPropertyName =
                    nameof(BallCatalogEntry.ItemId),
                Width = 130
            });

        _ballCatalogGrid.DataError += (_, e) =>
        {
            e.ThrowException = false;
        };
    }

    private void BindData()
    {
        _rulesGrid.DataSource = _rules;
        _ballCatalogGrid.DataSource = _balls;

        RefreshBallCombo();
        RefreshRulesStatus();
        RefreshBallCatalogStatus();
    }

    private void ApplySavedSettings()
    {
        _continuousAutoCatch.Checked =
            _settings.AutoCatchEnabled;

        _internalBallDelayMs.Value =
            Math.Clamp(
                _settings.CorpseBallDelayMs,
                (int)_internalBallDelayMs.Minimum,
                (int)_internalBallDelayMs.Maximum);
    }

    private async Task ConnectEverythingAsync()
    {
        if (_connectBusy)
            return;

        _connectBusy = true;
        _connect.Enabled = false;
        _mainStatus.Text = "Conectando...";

        try
        {
            StopMonitor(updateMainStatus: false);

            if (!AttachReaderOnly(showErrors: false))
                throw new InvalidOperationException(
                    "Não foi possível conectar ao PxG.");

            if (!_reader.IsCompatibleBuild)
            {
                throw new InvalidOperationException(
                    "O Compatibility Resolver não conseguiu validar o pxgme.exe atual.");
            }

            bool bridgeOk =
                await LoadBridgeAsync(showErrors: false);

            if (!bridgeOk)
            {
                throw new InvalidOperationException(
                    "A bridge interna não ficou pronta.");
            }

            StartMonitor();

            _mainStatus.Text =
                $"Conectado • PID {_reader.Process.Id} • Monitor ativo";

            _connect.Text = "Reconectar ao PxG";

            Log(
                $"CONNECT_ALL_OK pid={_reader.Process.Id}; " +
                $"auto_catch={(_continuousAutoCatch.Checked ? 1 : 0)}; " +
                $"delay_ms={(int)_internalBallDelayMs.Value}; " +
                $"rules={_rules.Count}");
        }
        catch (Exception ex)
        {
            _mainStatus.Text = "Falha ao conectar";

            Log($"CONNECT_ALL_FAILED {ex.Message}");

            MessageBox.Show(
                this,
                ex.Message,
                "Conectar ao PxG",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _connectBusy = false;
            _connect.Enabled = true;
        }
    }

    private bool AttachReaderOnly(bool showErrors)
    {
        try
        {
            StopMonitor(updateMainStatus: false);

            _bridgeReady = false;
            _bridgeStatus.Text =
                "Bridge: não carregada";

            _reader.Attach(compatibilityLog: Log);

            RefreshDeveloperState();

            Log($"ATTACH OK pid={_reader.Process.Id}");
            Log($"exe={_reader.ExePath}");
            Log($"sha256={_reader.ExeSha256}");
            Log($"known_build={_reader.IsKnownBuild}");
            Log($"compatible_build={_reader.IsCompatibleBuild}");

            if (_reader.CompatibilityProfile is { } profile)
            {
                Log(
                    $"compat_source={profile.Source}; " +
                    $"game_rva=0x{profile.GameRva:X}; " +
                    $"map_rva=0x{profile.MapPointerRva:X}; " +
                    $"lua_slot_rva=0x{profile.LuaInterfaceSlotRva:X}; " +
                    $"lua_pcall_rva=0x{profile.LuaPCallRva:X}; " +
                    $"lua_loadbuffer_rva=0x{profile.LuaLoadBufferXRva:X}");
            }
            Log($"module_base=0x{_reader.ModuleBase:X}");
            Log($"game=0x{_reader.GameAddress:X}");
            Log(
                $"map_ptr=0x{_reader.ReadMapPointer():X}");
            Log(
                $"CATCH_RULES path={CatchRuleStore.FilePath}; " +
                $"total={_rules.Count}");
            Log(
                $"BALL_CATALOG path={BallCatalogStore.FilePath}; " +
                $"configured={_balls.Count(x => x.ItemId > 0)}");

            if (!_reader.IsCompatibleBuild)
            {
                Log(
                    "ATENÇÃO: build não validado pelo Compatibility Resolver. " +
                    "Bridge e automação interna permanecem bloqueadas.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"ATTACH FALHOU: {ex.Message}");

            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Falha ao anexar Reader",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return false;
        }
    }

    private async Task<bool> LoadBridgeAsync(
        bool showErrors)
    {
        if (!_reader.IsAttached)
        {
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    "Conecte o Reader ao PxG primeiro.",
                    "Internal Bridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return false;
        }

        _bridgeStatus.Text =
            "Bridge: carregando...";

        try
        {
            string result =
                await _bridgeLoader.EnsureLoadedAsync(
                    _reader,
                    _bridgeClient,
                    Log);

            _bridgeReady = true;
            _bridgeStatus.Text =
                $"Bridge: {result}";

            Log($"BRIDGE_READY {result}");
            Log(
                "INTERNAL_BALL_READY " +
                "api=g_game.useInventoryItemWith " +
                "target=tile:getTopUseThing()");

            return true;
        }
        catch (Exception ex)
        {
            _bridgeReady = false;
            _bridgeStatus.Text =
                "Bridge: falhou";

            Log($"BRIDGE_FAILED {ex.Message}");

            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Internal Bridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return false;
        }
    }

    private void StartMonitor()
    {
        if (!_reader.IsAttached)
        {
            MessageBox.Show(
                this,
                "Reader não está conectado ao PxG.",
                "Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        StopMonitor(updateMainStatus: false);

        _monitor = new CatchMonitor(_reader)
        {
            PollMs = (int)_pollMs.Value
        };

        SyncWatchedPokemonNames();

        _monitor.Log += Log;

        _monitor.PlayerPositionUpdated += pos =>
            BeginInvoke(() =>
                _player.Text =
                    pos?.ToString() ?? "NA");

        _monitor.TargetUpdated += target =>
            BeginInvoke(() =>
            {
                if (target is null)
                {
                    _targetLabel.Text = "0";
                    _targetHp.Text = "-";
                    _targetPos.Text = "-";
                    _resolver.Text = "-";
                    return;
                }

                var name =
                    string.IsNullOrWhiteSpace(target.Name)
                        ? "nome?"
                        : target.Name;

                _targetLabel.Text =
                    $"{name} | {target.Id} / 0x{target.Id:X8}";

                _targetHp.Text =
                    target.Resolved
                        ? $"{target.HpPercent}%"
                        : "não resolvido";

                _targetPos.Text =
                    target.Resolved
                        ? target.Position.ToString()
                        : "-";

                _resolver.Text =
                    target.ResolverMode;
            });

        _monitor.DeathDetected += ev =>
            BeginInvoke(() =>
                Log(
                    $"MORTE CONFIRMADA id={ev.TargetId}; " +
                    $"name={ev.TargetName}; hp={ev.LastHp}%; " +
                    $"pos={ev.CorpsePosition}; reason={ev.Reason}"));

        _monitor.CorpseConfirmed += ev =>
            BeginInvoke(
                async () =>
                    await HandleCorpseAsync(ev));

        _monitorCts =
            new CancellationTokenSource();

        _ = Task.Run(
            () => _monitor.RunAsync(
                _monitorCts.Token));

        _status.Text =
            $"Reader: conectado PID {_reader.Process.Id} • monitor ativo";

        Log("MONITOR INICIADO");
    }

    private void StopMonitor(
        bool updateMainStatus)
    {
        try
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
        }
        catch
        {
        }

        _monitorCts = null;
        _monitor = null;

        if (_reader.IsAttached)
        {
            _status.Text =
                $"Reader: conectado PID {_reader.Process.Id} • monitor parado";

            if (updateMainStatus)
            {
                _mainStatus.Text =
                    $"Conectado • PID {_reader.Process.Id} • Monitor parado";
            }
        }
    }

    private async Task HandleCorpseAsync(
        CorpseEvent ev)
    {
        Log(
            $"CORPSE CONFIRMADO PARA BRIDGE id={ev.TargetId}; " +
            $"name={ev.TargetName}; thing=0x{ev.ThingPointer:X}; " +
            $"vt=0x{ev.ThingVTable:X}; " +
            $"thing_index={ev.ThingIndex}; " +
            $"pos={ev.Position}; " +
            $"after={ev.ConfirmedAfterMs}ms");

        if (!_bridgeReady)
        {
            Log(
                "CATCH_SKIPPED reason=bridge_not_ready");
            return;
        }

        if (_continuousAutoCatch.Checked)
        {
            if (string.IsNullOrWhiteSpace(
                ev.TargetName))
            {
                Log(
                    $"CATCH_SKIPPED id={ev.TargetId}; " +
                    "reason=target_name_unavailable");
                return;
            }

            if (!TryGetRule(
                ev.TargetName,
                out var rule))
            {
                Log(
                    $"CATCH_SKIPPED pokemon={ev.TargetName}; " +
                    $"id={ev.TargetId}; reason=no_rule");
                return;
            }

            if (!TryResolveConfiguredBall(
                rule.BallName,
                out var ball))
            {
                Log(
                    $"CATCH_SKIPPED pokemon={ev.TargetName}; " +
                    $"ball={rule.BallName}; " +
                    "reason=ball_item_id_not_configured");
                return;
            }

            await ExecuteInternalBallAsync(
                ev,
                ball);

            return;
        }

        if (_probeOnCorpse.Checked)
        {
            await RunBridgeProbeAsync(ev);
            return;
        }

        Log(
            $"CATCH_IDLE pokemon={ev.TargetName}; " +
            "auto_catch=off; diagnostic=off");
    }

    private async Task ExecuteInternalBallAsync(
        CorpseEvent ev,
        BallCatalogEntry ball)
    {
        if (_recentCatchAttempts.TryGetValue(
                ev.TargetId,
                out var previous) &&
            (DateTime.Now - previous)
                .TotalSeconds < 10)
        {
            Log(
                $"INTERNAL_BALL_ABORTED id={ev.TargetId}; " +
                "reason=duplicate_target");
            return;
        }

        _recentCatchAttempts[ev.TargetId] =
            DateTime.Now;

        foreach (var old in
            _recentCatchAttempts
                .Where(x =>
                    (DateTime.Now - x.Value)
                        .TotalSeconds > 30)
                .Select(x => x.Key)
                .ToArray())
        {
            _recentCatchAttempts.Remove(old);
        }

        await _ballActionGate.WaitAsync();

        try
        {
            int delay =
                (int)_internalBallDelayMs.Value;

            Log(
                $"INTERNAL_BALL_PRECHECK id={ev.TargetId}; " +
                $"pokemon={ev.TargetName}; pos={ev.Position}; " +
                $"corpse_thing=0x{ev.ThingPointer:X}; " +
                $"ball={ball.Name}; ball_item_id={ball.ItemId}; " +
                $"confirmed_after={ev.ConfirmedAfterMs}ms; " +
                $"extra_delay={delay}ms");

            if (delay > 0)
                await Task.Delay(delay);

            var response =
                await _bridgeClient.SendAsync(
                    _reader.Process.Id,
                    BridgeAction.UseBallOnCorpse,
                    ev.Position.X,
                    ev.Position.Y,
                    ev.Position.Z,
                    ball.ItemId,
                    timeoutMs: 2500);

            if (response.Status ==
                    BridgeStatus.Executed &&
                response.Detail0 == 5000)
            {
                Log(
                    $"INTERNAL_BALL_EXECUTED id={ev.TargetId}; " +
                    $"pokemon={ev.TargetName}; pos={ev.Position}; " +
                    $"ball={ball.Name}; ball_item_id={ball.ItemId}; " +
                    $"bridge_detail={response.Detail0}; " +
                    "no_mouse=1; no_keyboard=1");
            }
            else
            {
                Log(
                    $"INTERNAL_BALL_FAILED id={ev.TargetId}; " +
                    $"pokemon={ev.TargetName}; ball={ball.Name}; " +
                    $"ball_item_id={ball.ItemId}; " +
                    $"status={response.Status}; " +
                    $"detail0={response.Detail0}; " +
                    $"detail1={response.Detail1}; " +
                    $"detail1_text=" +
                    $"{BridgeProbeCatalog.ExplainDetail(response.Detail1)}");
            }
        }
        catch (Exception ex)
        {
            Log(
                $"INTERNAL_BALL_FAILED id={ev.TargetId}; " +
                $"pokemon={ev.TargetName}; " +
                $"exception={ex.Message}");
        }
        finally
        {
            _ballActionGate.Release();
        }
    }

    private async Task RunBridgeProbeAsync(
        CorpseEvent ev)
    {
        try
        {
            Log(
                $"BRIDGE_PROBE_BEGIN pos={ev.Position}; " +
                $"pokemon={ev.TargetName}");

            bool coreOk = false;

            foreach (var probe in
                BridgeProbeCatalog.Definitions)
            {
                var response =
                    await _bridgeClient.SendAsync(
                        _reader.Process.Id,
                        BridgeAction.ProbeBallApi,
                        ev.Position.X,
                        ev.Position.Y,
                        ev.Position.Z,
                        probe.Code,
                        timeoutMs: 2500);

                bool ok =
                    response.Status ==
                    BridgeStatus.Executed;

                Log(
                    $"BRIDGE_PROBE code={probe.Code}; " +
                    $"label={probe.Label}; " +
                    $"status={response.Status}; " +
                    $"result={(ok ? "PASS" : "NO")}; " +
                    $"{BridgeProbeCatalog.ExplainDetail(response.Detail1)}");

                if (probe.Code == 0)
                {
                    coreOk = ok;

                    if (!coreOk)
                    {
                        Log(
                            "BRIDGE_PROBE_ABORT: " +
                            "executor Lua básico falhou.");
                        break;
                    }
                }
            }

            if (coreOk)
                Log("BRIDGE_PROBE_END");
        }
        catch (Exception ex)
        {
            Log(
                $"BRIDGE_PROBE_FAILED {ex.Message}");
        }
    }

    private void AddOrUpdateRuleFromMain()
    {
        var pokemon =
            CatchRule.NormalizePokemonName(
                _rulePokemon.Text);

        if (pokemon.Length == 0)
        {
            MessageBox.Show(
                this,
                "Informe o nome do Pokémon.",
                "Adicionar Pokémon",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_ruleBall.SelectedItem
            is not BallCatalogEntry ball ||
            ball.ItemId <= 0)
        {
            MessageBox.Show(
                this,
                "Selecione uma Poké Ball com Item ID configurado.",
                "Adicionar Pokémon",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var existing =
            _rules.FirstOrDefault(x =>
                string.Equals(
                    CatchRule.NormalizePokemonName(
                        x.Pokemon),
                    pokemon,
                    StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            _rules.Remove(existing);

        _rules.Add(
            new CatchRule
            {
                Enabled = true,
                Pokemon = pokemon,
                BallName = ball.Name,
                BallItemId = ball.ItemId
            });

        SaveRules(showLog: false);
        SyncWatchedPokemonNames();
        RefreshRulesStatus();

        _rulePokemon.Clear();

        Log(
            $"CATCH_RULE_SET pokemon={pokemon}; " +
            $"ball={ball.Name}; " +
            $"ball_item_id={ball.ItemId}");
    }

    private void RemoveRule(CatchRule rule)
    {
        var pokemon = rule.Pokemon;

        _rules.Remove(rule);

        SaveRules(showLog: false);
        SyncWatchedPokemonNames();
        RefreshRulesStatus();

        Log(
            $"CATCH_RULE_REMOVED pokemon={pokemon}");
    }

    private bool TryGetRule(
        string pokemon,
        out CatchRule rule)
    {
        var match =
            _rules.FirstOrDefault(
                x => x.Matches(pokemon));

        if (match is null)
        {
            rule = new CatchRule();
            return false;
        }

        rule = match;
        return true;
    }

    private bool TryResolveConfiguredBall(
        string ballName,
        out BallCatalogEntry ball)
    {
        var normalized =
            BallCatalogStore.Normalize(ballName);

        var match =
            _balls.FirstOrDefault(x =>
                x.ItemId > 0 &&
                string.Equals(
                    BallCatalogStore.Normalize(x.Name),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            ball = new BallCatalogEntry();
            return false;
        }

        ball = match;
        return true;
    }

    private void SaveRules(bool showLog)
    {
        foreach (var rule in _rules)
        {
            if (TryResolveConfiguredBall(
                rule.BallName,
                out var ball))
            {
                rule.BallName = ball.Name;
                rule.BallItemId = ball.ItemId;
            }
        }

        CatchRuleStore.Save(_rules);

        if (showLog)
        {
            Log(
                $"CATCH_RULES_SAVED total={_rules.Count}; " +
                $"path={CatchRuleStore.FilePath}");
        }
    }

    private void SaveBallCatalog(bool showLog)
    {
        _ballCatalogGrid.EndEdit();

        foreach (var ball in _balls)
        {
            ball.Name =
                BallCatalogStore.Normalize(ball.Name);

            ball.ItemId =
                Math.Clamp(ball.ItemId, 0, 65535);
        }

        BallCatalogStore.Save(_balls);

        NormalizeRuleBallMappings();
        SaveRules(showLog: false);

        RefreshBallCombo();
        _rulesGrid.Refresh();
        RefreshBallCatalogStatus();

        if (showLog)
        {
            Log(
                $"BALL_CATALOG_SAVED configured=" +
                $"{_balls.Count(x => x.ItemId > 0)}; " +
                $"path={BallCatalogStore.FilePath}");
        }
    }

    private void NormalizeRuleBallMappings()
    {
        foreach (var rule in _rules)
        {
            var match =
                _balls.FirstOrDefault(x =>
                    x.ItemId > 0 &&
                    string.Equals(
                        BallCatalogStore.Normalize(x.Name),
                        BallCatalogStore.Normalize(
                            rule.BallName),
                        StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                rule.BallName = match.Name;
                rule.BallItemId = match.ItemId;
            }
        }
    }

    private void RefreshBallCombo()
    {
        string? previous =
            (_ruleBall.SelectedItem
                as BallCatalogEntry)?.Name;

        var configured =
            _balls
                .Where(x => x.ItemId > 0)
                .OrderBy(
                    x => x.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        _ruleBall.BeginUpdate();
        _ruleBall.Items.Clear();

        foreach (var ball in configured)
            _ruleBall.Items.Add(ball);

        if (configured.Length > 0)
        {
            int selectedIndex =
                Array.FindIndex(
                    configured,
                    x => string.Equals(
                        x.Name,
                        previous,
                        StringComparison.OrdinalIgnoreCase));

            if (selectedIndex < 0)
            {
                selectedIndex =
                    Array.FindIndex(
                        configured,
                        x => string.Equals(
                            x.Name,
                            "Ultra Ball",
                            StringComparison.OrdinalIgnoreCase));
            }

            _ruleBall.SelectedIndex =
                selectedIndex >= 0
                    ? selectedIndex
                    : 0;
        }

        _ruleBall.EndUpdate();
    }

    private void RefreshRulesStatus()
    {
        _rulesStatus.Text =
            _rules.Count == 0
                ? "Nenhum Pokémon configurado. Somente Pokémon desta lista serão capturados."
                : $"{_rules.Count} Pokémon configurado(s). Todos estão ativos.";
    }

    private void RefreshBallCatalogStatus()
    {
        _ballCatalogStatus.Text =
            $"{_balls.Count(x => x.ItemId > 0)} " +
            "Poké Ball(s) com Item ID configurado.";
    }

    private void SyncWatchedPokemonNames()
    {
        _monitor?.SetWatchedPokemonNames(
            _rules.Select(x => x.Pokemon));
    }

    private void RefreshDeveloperState()
    {
        if (!_reader.IsAttached)
            return;

        _status.Text =
            $"Reader: conectado PID {_reader.Process.Id}";

        _base.Text =
            $"0x{_reader.ModuleBase:X}";

        _game.Text =
            $"0x{_reader.GameAddress:X}";

        _map.Text =
            $"0x{_reader.ReadMapPointer():X}";

        _gameIds.Text =
            $"{_reader.ReadAttackingCreatureId()} / " +
            $"{_reader.ReadFollowingCreatureId()} / " +
            $"{_reader.ReadControllingCreatureId()}";

        var profile = _reader.CompatibilityProfile;

        _build.Text =
            profile is { Validated: true }
                ? $"COMPATÍVEL ({profile.Source})  {_reader.ExeSha256}"
                : $"INCOMPATÍVEL  {_reader.ExeSha256}";
    }

    private void SaveUserSettings()
    {
        _settings.AutoCatchEnabled =
            _continuousAutoCatch.Checked;

        _settings.CorpseBallDelayMs =
            (int)_internalBallDelayMs.Value;

        try
        {
            UserSettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log(
                $"SETTINGS_SAVE_FAILED {ex.Message}");
        }
    }

    private static void AddRow(
        TableLayoutPanel table,
        string name,
        Label value)
    {
        int row = table.RowCount++;

        table.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));

        var key = new Label
        {
            Text = name,
            AutoSize = true,
            Padding =
                new Padding(0, 3, 12, 3)
        };

        value.AutoSize = true;
        value.Padding =
            new Padding(0, 3, 0, 3);
        value.Text = "-";

        table.Controls.Add(key, 0, row);
        table.Controls.Add(value, 1, row);
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }

        var line =
            $"{DateTime.Now:HH:mm:ss.fff}  {message}";

        _log.AppendText(
            line + Environment.NewLine);

        // Keep the developer textbox responsive during long hunts.
        // The complete daily log remains on disk.
        const int MaxUiChars = 600_000;
        const int KeepUiChars = 450_000;

        if (_log.TextLength > MaxUiChars)
        {
            int remove =
                _log.TextLength - KeepUiChars;

            _log.Select(0, remove);
            _log.SelectedText = "";
            _log.SelectionStart =
                _log.TextLength;
            _log.ScrollToCaret();
        }

        try
        {
            var logs =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "logs");

            Directory.CreateDirectory(logs);

            var file =
                Path.Combine(
                    logs,
                    $"corpse-reader-{DateTime.Now:yyyyMMdd}.log");

            File.AppendAllText(
                file,
                line + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private void Shutdown()
    {
        SaveUserSettings();
        SaveBallCatalog(showLog: false);
        SaveRules(showLog: false);

        StopMonitor(updateMainStatus: false);
        _reader.Dispose();
    }
}
