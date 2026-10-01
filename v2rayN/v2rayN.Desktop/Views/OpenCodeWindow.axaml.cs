using v2rayN.Desktop.Base;

namespace v2rayN.Desktop.Views;

public partial class OpenCodeWindow : WindowBase<OpenCodeViewModel>
{
    public OpenCodeWindow()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RefreshModelsCmd, v => v.btnRefreshModels).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.TestConnectionCmd, v => v.btnTest).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SaveApiKeyCmd, v => v.btnSaveApiKey).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ClearApiKeyCmd, v => v.btnClearApiKey).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Enabled, v => v.chkEnabled.IsChecked).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.GatewayEnabled, v => v.chkGatewayEnabled.IsChecked).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.DefaultTarget, v => v.txtDefaultTarget.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.DefaultModel, v => v.txtDefaultModel.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.GatewayHost, v => v.txtGatewayHost.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.GatewayPort, v => v.txtGatewayPort.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ConnectTimeoutSeconds, v => v.txtConnectTimeout.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RequestTimeoutSeconds, v => v.txtRequestTimeout.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.MaxRetry, v => v.txtMaxRetry.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.MaxConcurrentRequests, v => v.txtMaxConcurrent.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.TargetName, v => v.txtTargetName.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.TargetBaseUrl, v => v.txtTargetBaseUrl.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.TargetCatalogUrl, v => v.txtTargetCatalogUrl.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ApiKeyInput, v => v.txtApiKey.Text).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.GatewayStatusText, v => v.txtGatewayStatus.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.EndpointText, v => v.txtEndpoint.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ProfileStatusText, v => v.txtProfile.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ProxyStatusText, v => v.txtProxy.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.EgressIpText, v => v.txtEgressIp.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.TestResultText, v => v.txtTestResult.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.TelemetryText, v => v.txtTelemetry.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ClientExampleText, v => v.txtClientExample.Text).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.Enabled, v => v.chkEnabled.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.GatewayEnabled, v => v.chkGatewayEnabled.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.DefaultTarget, v => v.txtDefaultTarget.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.DefaultModel, v => v.txtDefaultModel.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.GatewayHost, v => v.txtGatewayHost.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.GatewayPort, v => v.txtGatewayPort.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ConnectTimeoutSeconds, v => v.txtConnectTimeout.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.RequestTimeoutSeconds, v => v.txtRequestTimeout.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.MaxRetry, v => v.txtMaxRetry.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.MaxConcurrentRequests, v => v.txtMaxConcurrent.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.TargetName, v => v.txtTargetName.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.TargetBaseUrl, v => v.txtTargetBaseUrl.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.TargetCatalogUrl, v => v.txtTargetCatalogUrl.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ApiKeyInput, v => v.txtApiKey.Text).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ModelRows, v => v.grdModels.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedModelRow, v => v.grdModels.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ModelFilter, v => v.txtModelFilter.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.FilterFreeOnly, v => v.chkFreeOnly.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ModelApiStyleFilterIndex, v => v.cboApiStyleFilter.SelectedIndex).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ModelSortIndex, v => v.cboSort.SelectedIndex).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SelectedModelDisplay, v => v.txtSelectedModel.Text).DisposeWith(disposables);
        });
    }
}
