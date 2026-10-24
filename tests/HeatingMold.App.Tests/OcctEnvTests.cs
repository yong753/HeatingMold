using System;
using System.Collections.Generic;
using System.IO;
using HeatingMold.App;

namespace HeatingMold.App.Tests;

/// <summary>패키지 실행 리소스와 개발 실행 리소스의 검색 순서를 확인합니다.</summary>
public sealed class OcctEnvTests
{
    /// <summary>설치 폴더의 모델이 저장소 모델보다 먼저 선택됩니다.</summary>
    [Fact]
    public void ResolveMoldStp_PrefersPackagedModel()
    {
        WithTemporaryDirectory(root =>
        {
            string appDirectory = Path.Combine(root, "publish");
            string repositoryRoot = Path.Combine(root, "repository");
            string packagedModel = CreateFile(appDirectory, "resource", "Mold.stp");
            CreateFile(repositoryRoot, "resource", "Mold.stp");

            Assert.Equal(packagedModel, OcctEnv.ResolveMoldStp(appDirectory, repositoryRoot));
        });
    }

    /// <summary>설치 폴더에 모델이 없을 때 개발 저장소 모델을 찾습니다.</summary>
    [Fact]
    public void ResolveMoldStp_FallsBackToDevelopmentModel()
    {
        WithTemporaryDirectory(root =>
        {
            string appDirectory = Path.Combine(root, "publish");
            string repositoryRoot = Path.Combine(root, "repository");
            string developmentModel = CreateFile(repositoryRoot, "resource", "Mold.stp");

            Assert.Equal(developmentModel, OcctEnv.ResolveMoldStp(appDirectory, repositoryRoot));
        });
    }

    /// <summary>패키지와 개발 저장소의 Native DLL 폴더를 실행 경로에 포함합니다.</summary>
    [Fact]
    public void CollectRuntimeDirectories_IncludesPackagedAndDevelopmentDllFolders()
    {
        WithTemporaryDirectory(root =>
        {
            string appDirectory = Path.Combine(root, "publish");
            string repositoryRoot = Path.Combine(root, "repository");
            string packagedOcctBin = Path.Combine(appDirectory, "native", "occt", "bin");
            string packagedThirdPartyBin = Path.Combine(appDirectory, "native", "3rdparty", "vendor", "bin");
            string developmentOcctBin = Path.Combine(
                repositoryRoot, "third_party", "opencascade-7.9.3-vc14-64", "win64", "vc14", "bin");
            string developmentThirdPartyBin = Path.Combine(
                repositoryRoot, "third_party", "3rdparty-vc14-64", "vendor", "bin");
            CreateDll(packagedOcctBin, "TKernel.dll");
            CreateDll(packagedThirdPartyBin, "packaged.dll");
            CreateDll(developmentOcctBin, "TKBRep.dll");
            CreateDll(developmentThirdPartyBin, "development.dll");

            IReadOnlyList<string> directories = OcctEnv.CollectRuntimeDirectories(appDirectory, repositoryRoot);

            Assert.Contains(Path.GetFullPath(appDirectory), directories);
            Assert.Contains(Path.GetFullPath(packagedOcctBin), directories);
            Assert.Contains(Path.GetFullPath(packagedThirdPartyBin), directories);
            Assert.Contains(Path.GetFullPath(developmentOcctBin), directories);
            Assert.Contains(Path.GetFullPath(developmentThirdPartyBin), directories);
        });
    }

    /// <summary>파일 하나를 임시 디렉터리 아래에 만들고 전체 경로를 반환합니다.</summary>
    private static string CreateFile(string root, string folder, string fileName)
    {
        string directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "fixture");
        return path;
    }

    /// <summary>테스트용 DLL 파일을 지정 폴더에 만듭니다.</summary>
    private static void CreateDll(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), "fixture");
    }

    /// <summary>격리된 임시 폴더를 만들고 검사 뒤 제거합니다.</summary>
    private static void WithTemporaryDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), $"HeatingMold-OcctEnv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
