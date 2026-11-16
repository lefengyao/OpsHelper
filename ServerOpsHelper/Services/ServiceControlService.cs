using System;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// Windows 服务控制服务，支持本地服务的重启等操作
/// </summary>
public class ServiceControlService
{
    /// <summary>
    /// 重启指定的本地 Windows 服务
    /// </summary>
    /// <param name="serviceName">服务名（ServiceName，非显示名称）</param>
    /// <param name="timeoutSeconds">等待超时时间（秒）</param>
    public async Task<ServiceOperationResult> RestartServiceAsync(string serviceName, int timeoutSeconds = 60)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var controller = new ServiceController(serviceName);

                if (controller.Status == ServiceControllerStatus.Running)
                {
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(timeoutSeconds));
                }

                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(timeoutSeconds));

                return new ServiceOperationResult
                {
                    Success = true,
                    Message = $"服务 {serviceName} 重启成功。"
                };
            }
            catch (Exception ex)
            {
                return new ServiceOperationResult
                {
                    Success = false,
                    Message = $"服务 {serviceName} 重启失败：{ex.Message}"
                };
            }
        });
    }

    /// <summary>
    /// 获取指定服务的当前状态
    /// </summary>
    public ServiceControllerStatus? GetServiceStatus(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 服务操作结果
/// </summary>
public class ServiceOperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
