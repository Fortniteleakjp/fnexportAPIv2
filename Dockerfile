# ステージ1: ビルド環境
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# プロジェクトファイルをコピーして依存関係を復元
COPY ["FortnitePorting/FortnitePorting.csproj", "FortnitePorting/"]
COPY ["CUE4Parse/CUE4Parse/CUE4Parse.csproj", "CUE4Parse/CUE4Parse/"]
COPY ["CUE4Parse/CUE4Parse-Conversion/CUE4Parse-Conversion.csproj", "CUE4Parse/CUE4Parse-Conversion/"]
COPY ["EpicManifestParser/src/EpicManifestParser.ZlibngDotNetDecompressor/EpicManifestParser.ZlibngDotNetDecompressor.csproj", "EpicManifestParser/src/EpicManifestParser.ZlibngDotNetDecompressor/"]

# ソースコード全体をコピー
COPY . .

# アプリケーションをビルドして発行（依存関係の復元も含む）
WORKDIR /src/FortnitePorting
RUN dotnet publish -c Release -o /app/publish --self-contained false -r linux-x64

# ステージ2: 実行環境
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# 必要なネイティブライブラリの依存関係をインストール
RUN apt-get update && apt-get install -y \
    libicu-dev \
    ca-certificates \
    libfontconfig1 \
    libfreetype6 \
    libgl1 \
    && rm -rf /var/lib/apt/lists/*

# libsディレクトリを作成（Oodleライブラリ用）
RUN mkdir -p /app/libs /app/manifest /app/chunk_cache /app/mappings

# ビルドステージから発行されたファイルのみをコピー
COPY --from=build /app/publish .

# ボリュームを定義（永続化用）
VOLUME ["/app/libs", "/app/manifest", "/app/chunk_cache", "/app/mappings"]

# ASP.NET Coreのポート設定（環境変数PORTで変更可能、デフォルトは3849）
ENV PORT=3849
EXPOSE 3849

# Oodleライブラリのパスを環境変数で指定可能にする
# 使用方法: docker run -v /path/to/oodle:/app/libs ...
# プロジェクトルートを/appに設定（マニフェストファイルパスを使わない）
ENV PROJECT_ROOT=/app

# 2GB環境用の最適化設定
# SKIP_MAPPING=false でマッピングファイルをロード（2GB環境では可能）
ENV SKIP_MAPPING=false

# JIT最適化
ENV COMPlus_ReadyToRun=0
ENV COMPlus_TC_QuickJitForLoops=1

# .NETランタイムのガベージコレクション設定（2GB環境最適化）
ENV DOTNET_gcServer=1
ENV DOTNET_GCHeapCount=8
ENV DOTNET_GCConserveMemory=0
ENV DOTNET_GCHeapCount=16

# inotifyとファイル監視対策
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV DOTNET_hostBuilder__reloadConfigOnChange=false
ENV ASPNETCORE_hostBuilder__reloadConfigOnChange=false
ENV LD_LIBRARY_PATH=/app:/app/libs

# ulimit設定（ファイルディスクリプタ制限対策）
RUN echo "* soft nofile 65536" >> /etc/security/limits.conf && \
    echo "* hard nofile 65536" >> /etc/security/limits.conf

# アプリケーションのエントリーポイント
ENTRYPOINT ["dotnet", "FortnitePorting.dll"]
