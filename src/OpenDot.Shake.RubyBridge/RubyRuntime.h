// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

#pragma once

using namespace System;

namespace OpenDot::Shake::RubyBridge
{
public
ref class RubyRuntime abstract sealed
{
  public:
    static String ^ Evaluate(String ^ runtimeHome, String ^ source);
};
} // namespace OpenDot::Shake::RubyBridge
