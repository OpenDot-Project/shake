# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

require "json"
require "base64"

module Shake
  class DslError < StandardError
  end

  class TargetRef
    attr_reader :label

    def initialize(label)
      @label = label.freeze
      freeze
    end

    def to_s
      @label
    end
  end

  class TargetBuilder
    APPEND_FIELDS = %i[sources dependencies headers include_directories defines arguments outputs tools].freeze

    attr_reader :data

    def initialize(rule, name, label)
      @open = true
      @data = {
        "rule" => rule.to_s,
        "name" => name,
        "label" => label,
        "sources" => [],
        "dependencies" => [],
        "visibility" => ["private"],
        "options" => {}
      }
    end

    APPEND_FIELDS.each do |field|
      define_method(field) do |*items|
        ensure_open!
        normalized = items.flat_map { |item| item.is_a?(Array) ? item : [item] }
        normalized.each do |item|
          value = item.is_a?(TargetRef) ? item.label : item
          raise DslError, "#{field} expects String or TargetRef values" unless value.is_a?(String)
          @data[field.to_s] << value
        end
        @data[field.to_s].uniq!
        nil
      end
    end

    def visibility(*items)
      ensure_open!
      raise DslError, "visibility may be called only once" if @visibility_set
      values = items.flat_map { |item| item.is_a?(Array) ? item : [item] }
      raise DslError, "visibility expects at least one value" if values.empty?
      values.each do |value|
        unless value == :private || value == :public || value.is_a?(String)
          raise DslError, "visibility values must be :private, :public, or consumer labels"
        end
      end
      raise DslError, "visibility symbols cannot be combined with consumer labels" if values.any? { |v| v.is_a?(Symbol) } && values.length > 1
      @data["visibility"] = values.map(&:to_s)
      @visibility_set = true
      nil
    end

    def codegen(**options)
      set_once("codegen", options)
    end

    def timeout(value)
      set_once("timeout", value)
    end

    def seed(value)
      set_once("seed", value)
    end

    def shards(value)
      set_once("shards", value)
    end

    def c_standard(value)
      set_once("c_standard", value)
    end

    def cxx_standard(value)
      set_once("cxx_standard", value)
    end

    def toolchain(value)
      set_once("toolchain", value)
    end

    def artifact_type(value)
      set_once("artifact_type", value)
    end

    def optimization(value)
      set_once("optimization", value)
    end

    def debug_info(value)
      set_once("debug_info", value)
    end

    def warnings_as_errors(value)
      set_once("warnings_as_errors", value)
    end

    def sanitizer(value)
      set_once("sanitizer", value)
    end

    def build_tags(*values)
      append_option("build_tags", values)
    end

    def race(value)
      set_once("race", value)
    end

    def test_filter(value)
      set_once("test_filter", value)
    end

    def count(value)
      set_once("count", value)
    end

    def tool(value)
      set_once("tool", value.is_a?(TargetRef) ? value.label : value)
    end

    def environment(**values)
      set_once("environment", values)
    end

    def output(name)
      ensure_open!
      raise DslError, "output references are not available until the graph stage implements genrule outputs"
    end

    def close
      @open = false
      @data["sources"].uniq!
      @data["dependencies"].uniq!
      @data["headers"]&.uniq!
      @data["include_directories"]&.uniq!
      @data["defines"]&.uniq!
      @data["arguments"]&.uniq!
      @data["outputs"]&.uniq!
      @data["tools"]&.uniq!
      symbolize_to_json(@data)
    end

    private

    def ensure_open!
      raise DslError, "target builder cannot be mutated after its declaration block returns" unless @open
    end

    def set_once(name, value)
      ensure_open!
      raise DslError, "#{name} may be called only once" if @data["options"].key?(name)
      @data["options"][name] = value
      nil
    end

    def append_option(name, values)
      ensure_open!
      current = (@data["options"][name] ||= [])
      current.concat(values)
      current.uniq!
      nil
    end

    def symbolize_to_json(value)
      case value
      when Hash
        value.transform_keys(&:to_s).transform_values { |child| symbolize_to_json(child) }
      when Array
        value.map { |child| symbolize_to_json(child) }
      when Symbol
        value.to_s
      else
        value
      end
    end
  end

  class ModuleContext
    attr_reader :metadata, :dependencies, :overrides, :go_manifest

    def initialize(root)
      @root = root
      @metadata = nil
      @dependencies = []
      @overrides = []
      @go_manifest = nil
    end

    def project(name, version: nil, edition: 2026)
      raise DslError, "project may be declared only once" if @metadata
      raise DslError, "project name must be a lowercase package identifier" unless name.is_a?(String) && name.match?(/\A[a-z][a-z0-9_-]*\z/)
      raise DslError, "project version must be a String when provided" unless version.nil? || version.is_a?(String)
      raise DslError, "edition must be Integer 2026" unless edition == 2026
      @metadata = { "name" => name, "version" => version, "edition" => edition }
      nil
    end

    def dependency(name, version:, dev: false)
      raise DslError, "dependency name must be a String" unless name.is_a?(String)
      raise DslError, "dependency version must be a String" unless version.is_a?(String)
      raise DslError, "dev must be Boolean" unless dev == true || dev == false
      raise DslError, "duplicate dependency #{name}" if @dependencies.any? { |item| item["name"] == name }
      @dependencies << { "name" => name, "version" => version, "dev" => dev }
      nil
    end

    def local_override(name, path:)
      raise DslError, "local_override name and path must be Strings" unless name.is_a?(String) && path.is_a?(String)
      raise DslError, "duplicate local override #{name}" if @overrides.any? { |item| item["name"] == name }
      @overrides << { "name" => name, "path" => path }
      nil
    end

    def go_module(path)
      raise DslError, "go_module may be declared only once" if @go_manifest
      raise DslError, "go_module path must be a String" unless path.is_a?(String)
      @go_manifest = path
      nil
    end

    def evaluate_file(path)
      source = File.binread(path).force_encoding(Encoding::UTF_8)
      raise DslError, "#{path}: source is not valid UTF-8" unless source.valid_encoding?
      eval(source, binding, path, 1)
    end
  end

  class BuildContext
    RULES = %i[
      sol_library sol_binary sol_test c_library c_binary c_test
      cc_library cc_binary cc_test go_library go_binary go_test
      filegroup genrule
    ].freeze

    attr_reader :targets

    def initialize(root, package)
      @root = root
      @package = package
      @targets = []
      @default_targets = nil
    end

    def declared_default_targets
      @default_targets
    end

    RULES.each do |rule|
      define_method(rule) do |name, &block|
        declare_target(rule, name, &block)
      end
    end

    def default_targets(*labels)
      raise DslError, "default_targets may be declared only once" unless @default_targets.nil?
      values = labels.flat_map { |item| item.is_a?(Array) ? item : [item] }
      normalized = values.map { |item| item.is_a?(TargetRef) ? item.label : item }
      raise DslError, "default_targets values must be Strings or TargetRef values" unless normalized.all? { |item| item.is_a?(String) }
      @default_targets = normalized
      nil
    end

    def glob(*patterns, exclude: [], allow_empty: false)
      patterns = patterns.flat_map { |item| item.is_a?(Array) ? item : [item] }
      raise DslError, "glob patterns must be Strings" unless patterns.all? { |item| item.is_a?(String) }
      raise DslError, "glob exclude must be an Array<String>" unless exclude.is_a?(Array) && exclude.all? { |item| item.is_a?(String) }
      raise DslError, "allow_empty must be Boolean" unless allow_empty == true || allow_empty == false
      patterns.each { |pattern| validate_pattern(pattern) }
      exclude.each { |pattern| validate_pattern(pattern) }
      files = patterns.flat_map { |pattern| Dir.glob(pattern, base: @package, sort: true) }
      files.uniq!
      files.reject! { |path| excluded?(path, exclude) || excluded_by_default?(path) }
      files.each { |path| ensure_inside_workspace!(path) }
      raise DslError, "glob matched no files" if files.empty? && !allow_empty
      files.freeze
    end

    def evaluate_file(path)
      source = File.binread(path).force_encoding(Encoding::UTF_8)
      raise DslError, "#{path}: source is not valid UTF-8" unless source.valid_encoding?
      eval(source, binding, path, 1)
    end

    private

    def declare_target(rule, name, &block)
      raise DslError, "#{rule} target name must be a lowercase identifier" unless name.is_a?(String) && name.match?(/\A[a-z][a-z0-9_]*\z/)
      raise DslError, "#{rule} requires a block with exactly one builder parameter" unless block && block.arity == 1
      package = @package == "." ? "" : @package
      label = "#{package.empty? ? '//' : "//#{package}"}:#{name}"
      builder = TargetBuilder.new(rule, name, label)
      begin
        block.call(builder)
        data = builder.close
        raise DslError, "#{label} declares duplicate sources" unless data["sources"].uniq.length == data["sources"].length
        raise DslError, "#{label} has no sources" if data["sources"].empty? && !%w[filegroup genrule].include?(rule.to_s)
        @targets << data
        TargetRef.new(label)
      ensure
        builder.close unless builder.instance_variable_get(:@open) == false
      end
    end

    def validate_pattern(pattern)
      raise DslError, "glob pattern cannot be empty" if pattern.empty?
      normalized = pattern.tr("\\", "/")
      raise DslError, "glob pattern must be relative" if normalized.start_with?("/") || normalized.match?(/\A[A-Za-z]:/)
      raise DslError, "glob pattern cannot contain a parent path segment" if normalized.split("/").include?("..")
    end

    def excluded?(path, excludes)
      excludes.any? { |pattern| File.fnmatch?(pattern, path, File::FNM_PATHNAME) }
    end

    def excluded_by_default?(path)
      path.split("/").any? { |part| %w[out vendor .git .codex].include?(part) }
    end

    def ensure_inside_workspace!(path)
      absolute = File.realpath(File.join(@root, @package, path))
      root = File.realpath(@root)
      prefix = root.end_with?(File::SEPARATOR) ? root : root + File::SEPARATOR
      raise DslError, "glob path escapes the workspace: #{path}" unless absolute == root || absolute.start_with?(prefix)
    end
  end

  class Session
    def self.evaluate(payload)
      root = File.realpath(payload.fetch("root"))
      module_file = File.expand_path(payload.fetch("module_file"), root)
      ensure_workspace_file!(module_file, root)
      module_context = ModuleContext.new(root)
      module_context.evaluate_file(module_file)

      build_files = payload.fetch("build_files")
      targets = []
      defaults = []
      build_files.each do |relative|
        path = File.expand_path(relative, root)
        ensure_workspace_file!(path, root)
        package = File.dirname(relative).tr("\\", "/")
        package = "." if package == "."
        context = BuildContext.new(root, package)
        context.evaluate_file(path)
        targets.concat(context.targets)
        defaults.concat(context.declared_default_targets) unless context.declared_default_targets.nil?
      end

      labels = {}
      targets.each do |target|
        raise DslError, "duplicate target label #{target.fetch('label')}" if labels.key?(target.fetch("label"))
        labels[target.fetch("label")] = true
      end

      JSON.generate({
        "module" => {
          "metadata" => module_context.metadata,
          "dependencies" => module_context.dependencies,
          "local_overrides" => module_context.overrides,
          "go_manifest" => module_context.go_manifest
        },
        "build_files" => build_files,
        "default_targets" => defaults,
        "targets" => targets
      })
    end

    def self.ensure_workspace_file!(path, root)
      full = File.realpath(path)
      prefix = root.end_with?(File::SEPARATOR) ? root : root + File::SEPARATOR
      raise DslError, "script path escapes the workspace: #{path}" unless full == root || full.start_with?(prefix)
      raise DslError, "expected a regular file: #{path}" unless File.file?(full)
    end

    private_class_method :ensure_workspace_file!
  end
end
