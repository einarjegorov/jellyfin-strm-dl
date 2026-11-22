DOTNET ?= mise exec -- dotnet
PYTHON ?= python3
REPOSITORY ?= einarjegorov/jellyfin-strm-dl
TEST_JELLYFIN_VERSION ?= 12.0.0
PROJECT = src/Jellyfin.Plugin.StrmDownload/Jellyfin.Plugin.StrmDownload.csproj
DLL = Jellyfin.Plugin.StrmDownload.dll

.PHONY: build test package

build:
	$(DOTNET) build $(PROJECT) --configuration Release --disable-build-servers -m:1
	mkdir -p artifacts
	cp src/Jellyfin.Plugin.StrmDownload/bin/Release/net10.0/$(DLL) artifacts/$(DLL)
	cd artifacts && sha256sum $(DLL) > SHA256SUMS

test:
	$(DOTNET) test Jellyfin.StrmDownload.slnx --configuration Release --disable-build-servers -m:1 -p:JellyfinTestVersion=$(TEST_JELLYFIN_VERSION)
	$(PYTHON) -m unittest discover -s tests/packaging

package: build
	$(PYTHON) scripts/package.py --repository "$(REPOSITORY)" $(if $(TAG),--tag "$(TAG)")
