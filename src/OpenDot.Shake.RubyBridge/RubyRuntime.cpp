// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

#include "RubyRuntime.h"

#include <windows.h>

#include <cstdint>
#include <memory>
#include <stdexcept>
#include <string>

#include <msclr/marshal_cppstd.h>
#include <vcclr.h>

using namespace System;
using namespace System::IO;
using namespace System::Runtime::InteropServices;
using namespace System::Text;

namespace OpenDot::Shake::RubyBridge
{
namespace
{
using RubyValue = std::uintptr_t;
using RubySysInit = void(__cdecl *)(int *, char ***);
using RubyInitStack = void(__cdecl *)(const void *);
using RubyInit = void(__cdecl *)();
using RubyInitLoadPath = void(__cdecl *)();
using RubyScript = void(__cdecl *)(const char *);
using RubyOptions = void *(__cdecl *)(int, char **);
using RubyExecNode = int(__cdecl *)(void *);
using RubyEvalStringProtect = RubyValue(__cdecl *)(const char *, int *);
using RubyErrorInfo = RubyValue(__cdecl *)();
using RubyObjectAsString = RubyValue(__cdecl *)(RubyValue);
using RubyStringValueCStr = char *(__cdecl *)(volatile RubyValue *);
using RubyCleanup = int(__cdecl *)(int);

class DynamicLibrary final
{
  public:
    explicit DynamicLibrary(const std::wstring &path)
    {
        handle_ =
            LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);

        if (handle_ == nullptr)
        {
            const auto error = GetLastError();
            throw std::runtime_error("LoadLibraryExW failed with Win32 error " + std::to_string(error));
        }
    }

    DynamicLibrary(const DynamicLibrary &) = delete;
    DynamicLibrary &operator=(const DynamicLibrary &) = delete;

    ~DynamicLibrary()
    {
        if (handle_ != nullptr)
        {
            FreeLibrary(handle_);
        }
    }

    template <typename TFunction> [[nodiscard]] TFunction Require(const char *name) const
    {
        const auto symbol = GetProcAddress(handle_, name);
        if (symbol == nullptr)
        {
            throw std::runtime_error(std::string("CRuby export is missing: ") + name);
        }

        return reinterpret_cast<TFunction>(symbol);
    }

  private:
    HMODULE handle_{};
};

class DllDirectory final
{
  public:
    explicit DllDirectory(const std::wstring &path)
    {
        cookie_ = AddDllDirectory(path.c_str());
        if (cookie_ == nullptr)
        {
            const auto error = GetLastError();
            throw std::runtime_error("AddDllDirectory failed with Win32 error " + std::to_string(error));
        }
    }

    DllDirectory(const DllDirectory &) = delete;
    DllDirectory &operator=(const DllDirectory &) = delete;

    ~DllDirectory()
    {
        if (cookie_ != nullptr)
        {
            RemoveDllDirectory(cookie_);
        }
    }

  private:
    DLL_DIRECTORY_COOKIE cookie_{};
};

[[nodiscard]] std::wstring FindRubyDll(String ^ runtimeHome)
{
    if (String::IsNullOrWhiteSpace(runtimeHome))
    {
        throw gcnew ArgumentException("The CRuby runtime directory is required.", "runtimeHome");
    }

    auto fullPath = Path::GetFullPath(runtimeHome);
    if (File::Exists(fullPath) &&
        String::Equals(Path::GetExtension(fullPath), ".dll", StringComparison::OrdinalIgnoreCase))
    {
        return msclr::interop::marshal_as<std::wstring>(fullPath);
    }

    auto binDirectory = Directory::Exists(Path::Combine(fullPath, "bin")) ? Path::Combine(fullPath, "bin") : fullPath;

    if (!Directory::Exists(binDirectory))
    {
        throw gcnew DirectoryNotFoundException("The CRuby runtime directory does not exist: " + fullPath);
    }

    auto candidates = Directory::GetFiles(binDirectory, "*ruby*.dll", SearchOption::TopDirectoryOnly);
    auto matching = gcnew System::Collections::Generic::List<String ^>();
    for each (String ^ candidate in candidates)
    {
        auto name = Path::GetFileName(candidate);
        if (name->IndexOf("rubygems", StringComparison::OrdinalIgnoreCase) < 0 &&
            name->IndexOf("readline", StringComparison::OrdinalIgnoreCase) < 0 &&
            name->IndexOf("openssl", StringComparison::OrdinalIgnoreCase) < 0)
        {
            matching->Add(candidate);
        }
    }

    if (matching->Count != 1)
    {
        throw gcnew InvalidOperationException("Expected exactly one CRuby VM DLL in " + binDirectory + "; found " +
                                              matching->Count.ToString() + ".");
    }

    return msclr::interop::marshal_as<std::wstring>(matching[0]);
}

[[nodiscard]] String ^
    DecodeRubyValue(RubyValue value, const RubyObjectAsString objectAsString, const RubyStringValueCStr stringValueCStr)
{
    auto stringValue = objectAsString(value);
    volatile RubyValue rootedString = stringValue;
    const auto utf8 = stringValueCStr(&rootedString);
    if (utf8 == nullptr)
    {
        return String::Empty;
    }

    return Marshal::PtrToStringUTF8(IntPtr(const_cast<char *>(utf8)));
}

[[nodiscard]] std::string EncodeUtf8(String ^ value)
{
    if (value->IndexOf('\0') >= 0)
    {
        throw gcnew ArgumentException("Ruby source cannot contain a NUL character.", "source");
    }

    auto bytes = Encoding::UTF8->GetBytes(value);
    if (bytes->Length == 0)
    {
        return std::string(1, '\0');
    }

    pin_ptr<Byte> pinned = &bytes[0];
    std::string encoded(reinterpret_cast<const char *>(pinned), static_cast<std::size_t>(bytes->Length));
    encoded.push_back('\0');
    return encoded;
}
} // namespace

String ^ RubyRuntime::Evaluate(String ^ runtimeHome, String ^ source)
{
    if (source == nullptr)
    {
        throw gcnew ArgumentNullException("source");
    }

    try
    {
        const auto libraryPath = FindRubyDll(runtimeHome);
        const auto separator = libraryPath.find_last_of(L"\\/");
        const auto binDirectory = separator == std::wstring::npos ? std::wstring() : libraryPath.substr(0, separator);

        if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
        {
            throw std::runtime_error("SetDefaultDllDirectories failed with Win32 error " +
                                     std::to_string(GetLastError()));
        }

        DllDirectory dependencyDirectory(binDirectory);
        auto runtimeRoot = Path::GetFullPath(runtimeHome);
        if (File::Exists(runtimeRoot))
        {
            auto runtimeBin = Path::GetDirectoryName(runtimeRoot);
            auto parent = Directory::GetParent(runtimeBin);
            if (parent != nullptr)
            {
                runtimeRoot = parent->FullName;
            }
        }
        else if (String::Equals(Path::GetFileName(runtimeRoot), "bin", StringComparison::OrdinalIgnoreCase))
        {
            auto parent = Directory::GetParent(runtimeRoot);
            if (parent != nullptr)
            {
                runtimeRoot = parent->FullName;
            }
        }

        // RubyInstaller places its UCRT support DLLs below this directory.
        auto mingwBin = Path::Combine(Path::Combine(Path::Combine(runtimeRoot, "msys64"), "ucrt64"), "bin");
        std::unique_ptr<DllDirectory> mingwDependencies;
        if (Directory::Exists(mingwBin))
        {
            mingwDependencies = std::make_unique<DllDirectory>(msclr::interop::marshal_as<std::wstring>(mingwBin));
        }

        DynamicLibrary ruby(libraryPath);

        const auto rubySysInit = ruby.Require<RubySysInit>("ruby_sysinit");
        const auto rubyInitStack = ruby.Require<RubyInitStack>("ruby_init_stack");
        const auto rubyInit = ruby.Require<RubyInit>("ruby_init");
        const auto rubyInitLoadPath = ruby.Require<RubyInitLoadPath>("ruby_init_loadpath");
        const auto rubyScript = ruby.Require<RubyScript>("ruby_script");
        const auto rubyOptions = ruby.Require<RubyOptions>("ruby_options");
        const auto rubyExecNode = ruby.Require<RubyExecNode>("ruby_exec_node");
        const auto rubyEvalStringProtect = ruby.Require<RubyEvalStringProtect>("rb_eval_string_protect");
        const auto rubyErrorInfo = ruby.Require<RubyErrorInfo>("rb_errinfo");
        const auto rubyObjectAsString = ruby.Require<RubyObjectAsString>("rb_obj_as_string");
        const auto rubyStringValueCStr = ruby.Require<RubyStringValueCStr>("rb_string_value_cstr");
        const auto rubyCleanup = ruby.Require<RubyCleanup>("ruby_cleanup");

        char systemExecutableName[] = "shake-ruby-host";
        char executableName[] = "shake-ruby-host";
        char disableGems[] = "--disable-gems";
        char suppressStartupWarnings[] = "-W0";
        char evalOption[] = "-e";
        char emptyProgram[] = "";
        char *systemArgumentValues[] = {systemExecutableName, nullptr};
        char **systemArgumentVector = systemArgumentValues;
        int systemArgumentCount = 1;
        char *argumentValues[] = {executableName, disableGems,  suppressStartupWarnings,
                                  evalOption,     emptyProgram, nullptr};
        rubySysInit(&systemArgumentCount, &systemArgumentVector);
        rubyInitStack(&systemArgumentCount);
        rubyInit();

        bool vmInitialized = true;
        try
        {
            rubyInitLoadPath();
            const auto startupNode = rubyOptions(5, argumentValues);
            if (startupNode == nullptr || rubyExecNode(startupNode) != 0)
            {
                throw std::runtime_error("CRuby failed to initialize its command-line runtime.");
            }

            rubyScript(executableName);
            auto sourceWithWarnings = String::Concat("$VERBOSE = true", Environment::NewLine, source);
            auto encodedSource = EncodeUtf8(sourceWithWarnings);
            int rubyState = 0;
            const auto result = rubyEvalStringProtect(encodedSource.c_str(), &rubyState);
            const auto value = rubyState == 0 ? result : rubyErrorInfo();
            auto managedResult = DecodeRubyValue(value, rubyObjectAsString, rubyStringValueCStr);
            rubyCleanup(0);
            vmInitialized = false;
            return managedResult;
        }
        finally
        {
            if (vmInitialized)
            {
                rubyCleanup(1);
            }
        }
    }
    catch (const std::exception &error)
    {
        throw gcnew InvalidOperationException(gcnew String(error.what()));
    }
}
} // namespace OpenDot::Shake::RubyBridge
