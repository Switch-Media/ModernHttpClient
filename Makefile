MDTOOL ?= /Applications/Visual\ Studio.app/Contents/MacOS/vstool
NUSPEC_FILE ?= ModernHttpClient.nuspec

.PHONY: all clean

all: ModernHttpClient.iOS64.dll ModernHttpClient.Android.dll ModernHttpClient.Portable.dll

release-pack: version-bump package

package: ModernHttpClient.iOS64.dll ModernHttpClient.Android.dll ModernHttpClient.Portable.dll
	nuget pack
	mv modernhttpclient*.nupkg ~/.local/share/NuGet/Cache/

ModernHttpClient.Android.dll: 
	$(MDTOOL) build -c:Release ./src/ModernHttpClient/ModernHttpClient.Android.csproj
	mkdir -p ./build/MonoAndroid
	mv ./src/ModernHttpClient/bin/Release/MonoAndroid/Modern* ./build/MonoAndroid

ModernHttpClient.iOS64.dll:
	$(MDTOOL) build -c:Release ./src/ModernHttpClient/ModernHttpClient.iOS64.csproj
	mkdir -p ./build/Xamarin.iOS10
	mv ./src/ModernHttpClient/bin/Release/Xamarin.iOS10/Modern* ./build/Xamarin.iOS10

ModernHttpClient.Portable.dll:
	$(MDTOOL) build -c:Release ./src/ModernHttpClient/ModernHttpClient.Portable.csproj
	mkdir -p ./build/Portable-Net45+WinRT45+WP8+WPA81
	mv ./src/ModernHttpClient/bin/Release/Portable-Net45+WinRT45+WP8+WPA81/Modern* ./build/Portable-Net45+WinRT45+WP8+WPA81

clean:
	$(MDTOOL) build -t:Clean ModernHttpClient.sln
	rm *.dll
	rm -rf build
	
version-bump:
	$(eval version = $(shell grep '<version>' $(NUSPEC_FILE) | sed "s@.*<version>\(.*\)</version>.*@\1@"))
	$(eval majorMinor = $(shell echo $(version) | rev | cut -d'.' -f2- | rev))
	$(eval buildNumber = $(shell echo $(version) | rev | cut -d'.' -f 1 | rev))
	$(eval newVersion = $(majorMinor).$(shell expr $(buildNumber) + 1))
	@echo version updated to $(newVersion)
	$(shell xmlstarlet ed -L -N N="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd" -u '/N:package/N:metadata/N:version' -v $(newVersion) $(NUSPEC_FILE))
