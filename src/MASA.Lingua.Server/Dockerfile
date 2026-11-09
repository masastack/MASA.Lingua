# 请参阅 https://aka.ms/customizecontainer 以了解如何自定义调试容器，以及 Visual Studio 如何使用此 Dockerfile 生成映像以更快地进行调试。

# 此阶段用于在快速模式(默认为调试配置)下从 VS 运行时
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# 发布产物目录必须对「实际运行用户」可写。默认的 {ContentRoot}/App_Data/i18n 落在 /app 下，只有镜像内的运行用户能写；
# 一旦编排层覆盖了运行用户（K8s runAsUser / compose user: / 平台随机 UID），/app 就不可写，
# 启动恢复与发布都会抛 UnauthorizedAccessException: Access to the path '/app/App_Data' is denied。
# 所以把目录挪到 /data/i18n，属主是运行用户，并设为 1777（同 /tmp：任意 UID 可建文件，粘滞位防互删），
# 再用环境变量把 Lingua:BundleDirectory 指过去（部署侧可用同名环境变量覆盖，例如指向挂载卷）。
# 该目录只是重启恢复用的镜像，权威数据在数据库的发布快照里，容器重建后会从快照自动补回，因此不必持久化。
# 注意：只读根文件系统（readOnlyRootFilesystem）下这里同样写不了，需要在部署侧把 /data/i18n 挂成 emptyDir/卷。
# DataProtection 密钥目录同模式建在 /data/dataprotection：认证 cookie 与 OIDC correlation/nonce 状态由它加密。
# 与 /data/i18n 不同，它必须跨容器重建持久 —— 部署清单把该路径挂成卷（或用 DataProtection__KeysDirectory 覆盖）；
# 不挂则密钥留在容器可写层，容器重建后全员登出（进程重启不受影响）。只读根文件系统下同样需要挂卷。
# 顺带补上 libgssapi-krb5-2：容器内缺 Kerberos/GSSAPI 库时，任何走到 Negotiate 认证的组件都会
# 打 "Cannot load library libgssapi_krb5.so.2" 警告（当前无害，装上消掉，避免排查干扰）。
# 注意必须排在 USER appuser 之前：apt-get 与用户管理需要 root。
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
# 运行用户对齐 specdoc 的挂卷属主约定：钉住 UID 1000（运维侧挂载卷属主惯例）。
# 实测同基础设施下 specdoc（UID 1000）挂卷正常；镜像默认 $APP_UID(1654) 在生产被拒（EACCES）。
# 保留 1777（specdoc 为 appuser 0755）：不影响 UID-1000 惯例匹配（挂载后镜像层被遮蔽），
# 保住「任意 runAsUser 覆盖可写」的文档承诺。
RUN set -eux; \
    if id -u 1000 >/dev/null 2>&1; then \
        existing="$(id -nu 1000)"; \
        if [ "$existing" != "appuser" ]; then \
            usermod -l appuser -d /home/appuser "$existing"; \
            groupmod -n appuser "$(id -gn 1000)"; \
        fi; \
    else \
        useradd --create-home --uid 1000 appuser; \
    fi; \
    mkdir -p /home/appuser /data/i18n /data/dataprotection /data/logs /data/dumps; \
    chown -R appuser:appuser /data /home/appuser; \
    chmod 1777 /data/i18n /data/dataprotection /data/logs /data/dumps
ENV Lingua__BundleDirectory=/data/i18n
ENV DataProtection__KeysDirectory=/data/dataprotection
ENV Lingua__LogsDirectory=/data/logs

# GC 内存防线：Server GC 在容器里默认按「机器总内存」感知堆上限，cgroup limit 超了才知道停手 ——
# 导入 / 发布 / 全库页面这类内存尖峰会在触发 GC 前就撞穿 limit，被 OOMKilled（SIGKILL）。
# GCHeapHardLimitPercent=50 把堆硬上限钉在 cgroup limit 的一半，给 EF 跟踪快照、JSON 字节、
# Blazor 渲染树等非堆内存留出余量；宁可 GC 多跑几次变慢，也不做无痕死亡。
# DynamicAdaptationMode=0 关闭 DAC 动态堆目标：它的启发式在「尖峰式分配」下反应滞后，反而放大尖峰。
ENV DOTNET_GCHeapHardLimitPercent=50
ENV DOTNET_GCDynamicAdaptationMode=0

# Crash dump：未处理异常导致的进程死亡时自动落一份 minidump 到 /data/dumps（带堆，可用 dotnet-dump 分析
# 死亡瞬间的托管堆）。%p %t 占位避免多次覆盖。目录与 /data/i18n 同模式：任意 UID 可写 + 粘滞位。
# 只读根文件系统下同样需要部署侧把 /data/dumps 挂成卷；挂不了则 dump 写不出来，但进程照常崩溃退出。
ENV DOTNET_DbgEnableMiniDump=1
ENV DOTNET_DbgMiniDumpType=2
ENV DOTNET_DbgMiniDumpName=/data/dumps/lingua_%p_%t.dmp
ENV DOTNET_CreateDumpDiagnostics=1
# Blazor 电路详细报错：电路内未处理异常把完整堆栈回传浏览器控制台（默认只有笼统信息）。
# 内网管理后台无泄露顾虑；注意只对「异常」生效——挂起无异常可报，靠 PmProjectDirectory 的请求超时兜底。
ENV ASPNETCORE_DETAILEDERRORS=true

USER appuser
ENV HOME=/home/appuser
WORKDIR /app
EXPOSE 8080
EXPOSE 8081



# 此阶段用于生成服务项目
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props","."]
COPY ["src/MASA.Lingua.Server/MASA.Lingua.Server.csproj","src/MASA.Lingua.Server/"]
COPY ["src/MASA.Lingua.Core/MASA.Lingua.Core.csproj","src/MASA.Lingua.Core/"]
COPY ["src/Storage/MASA.Lingua.Storage.EfCore/MASA.Lingua.Storage.EfCore.csproj","src/Storage/MASA.Lingua.Storage.EfCore/"]
COPY ["src/MASA.Lingua.AI/MASA.Lingua.AI.csproj","src/MASA.Lingua.AI/"]
COPY ["src/Storage/MASA.Lingua.Storage.Migrations.PostgreSql/MASA.Lingua.Storage.Migrations.PostgreSql.csproj","src/Storage/MASA.Lingua.Storage.Migrations.PostgreSql/"]
RUN dotnet restore "./src/MASA.Lingua.Server/MASA.Lingua.Server.csproj"
COPY . .
WORKDIR "/src/src/MASA.Lingua.Server"
RUN dotnet build "./MASA.Lingua.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

# 此阶段用于发布要复制到最终阶段的服务项目
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MASA.Lingua.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# 此阶段在生产中使用，或在常规模式下从 VS 运行时使用(在不使用调试配置时为默认值)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MASA.Lingua.Server.dll"]