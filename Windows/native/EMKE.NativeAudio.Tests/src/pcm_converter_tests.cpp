#include "pcm_converter.hpp"

#include <algorithm>
#include <cfenv>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <limits>
#include <map>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <variant>
#include <vector>

#ifndef EMKE_AUDIO_PCM_FIXTURE_DIRECTORY
#error "EMKE_AUDIO_PCM_FIXTURE_DIRECTORY must name the shared audio fixtures"
#endif

namespace {

class TestContext {
 public:
  void expect(bool condition, std::string_view expression, int line) {
    if (condition) {
      return;
    }
    ++failures_;
    std::cerr << line << ": expected " << expression << '\n';
  }

  [[nodiscard]] int failures() const {
    return failures_;
  }

 private:
  int failures_ = 0;
};

#define EXPECT(context, expression) \
  (context).expect((expression), #expression, __LINE__)

struct JsonValue {
  using Array = std::vector<JsonValue>;
  using Object = std::map<std::string, JsonValue, std::less<>>;
  using Storage =
      std::variant<std::nullptr_t, bool, double, std::string, Array, Object>;

  Storage storage;
};

class JsonParser {
 public:
  explicit JsonParser(std::string_view text) : text_(text) {}

  JsonValue parse() {
    skip_whitespace();
    JsonValue result = parse_value(0u);
    skip_whitespace();
    if (position_ != text_.size()) {
      fail("trailing content");
    }
    return result;
  }

 private:
  [[noreturn]] void fail(std::string_view reason) const {
    throw std::runtime_error(
        "JSON parse error at byte " + std::to_string(position_) + ": " +
        std::string(reason));
  }

  void skip_whitespace() {
    while (position_ < text_.size()) {
      const char character = text_[position_];
      if (character != ' ' && character != '\t' && character != '\n' &&
          character != '\r') {
        return;
      }
      ++position_;
    }
  }

  JsonValue parse_value(std::size_t depth) {
    if (depth > 64u) {
      fail("nesting is too deep");
    }
    if (position_ == text_.size()) {
      fail("missing value");
    }

    switch (text_[position_]) {
      case '{':
        return JsonValue{parse_object(depth + 1u)};
      case '[':
        return JsonValue{parse_array(depth + 1u)};
      case '"':
        return JsonValue{parse_string()};
      case 't':
        parse_literal("true");
        return JsonValue{true};
      case 'f':
        parse_literal("false");
        return JsonValue{false};
      case 'n':
        parse_literal("null");
        return JsonValue{nullptr};
      default:
        return JsonValue{parse_number()};
    }
  }

  JsonValue::Object parse_object(std::size_t depth) {
    ++position_;
    skip_whitespace();
    JsonValue::Object result;
    if (consume('}')) {
      return result;
    }

    while (true) {
      if (position_ == text_.size() || text_[position_] != '"') {
        fail("object key must be a string");
      }
      std::string key = parse_string();
      skip_whitespace();
      if (!consume(':')) {
        fail("missing ':' after object key");
      }
      skip_whitespace();
      auto [iterator, inserted] =
          result.emplace(std::move(key), parse_value(depth));
      static_cast<void>(iterator);
      if (!inserted) {
        fail("duplicate object key");
      }
      skip_whitespace();
      if (consume('}')) {
        return result;
      }
      if (!consume(',')) {
        fail("missing ',' between object members");
      }
      skip_whitespace();
    }
  }

  JsonValue::Array parse_array(std::size_t depth) {
    ++position_;
    skip_whitespace();
    JsonValue::Array result;
    if (consume(']')) {
      return result;
    }

    while (true) {
      result.push_back(parse_value(depth));
      skip_whitespace();
      if (consume(']')) {
        return result;
      }
      if (!consume(',')) {
        fail("missing ',' between array elements");
      }
      skip_whitespace();
    }
  }

  std::string parse_string() {
    ++position_;
    std::string result;
    while (position_ < text_.size()) {
      const unsigned char character =
          static_cast<unsigned char>(text_[position_++]);
      if (character == '"') {
        return result;
      }
      if (character < 0x20u) {
        fail("unescaped control character");
      }
      if (character != '\\') {
        result.push_back(static_cast<char>(character));
        continue;
      }
      if (position_ == text_.size()) {
        fail("incomplete escape");
      }
      switch (text_[position_++]) {
        case '"':
          result.push_back('"');
          break;
        case '\\':
          result.push_back('\\');
          break;
        case '/':
          result.push_back('/');
          break;
        case 'b':
          result.push_back('\b');
          break;
        case 'f':
          result.push_back('\f');
          break;
        case 'n':
          result.push_back('\n');
          break;
        case 'r':
          result.push_back('\r');
          break;
        case 't':
          result.push_back('\t');
          break;
        case 'u':
          parse_unicode_escape(result);
          break;
        default:
          fail("unsupported escape");
      }
    }
    fail("unterminated string");
  }

  void parse_unicode_escape(std::string& output) {
    if (text_.size() - position_ < 4u) {
      fail("incomplete unicode escape");
    }
    std::uint32_t value = 0u;
    for (std::size_t index = 0u; index < 4u; ++index) {
      value <<= 4u;
      const char digit = text_[position_++];
      if (digit >= '0' && digit <= '9') {
        value += static_cast<std::uint32_t>(digit - '0');
      } else if (digit >= 'a' && digit <= 'f') {
        value += static_cast<std::uint32_t>(digit - 'a' + 10);
      } else if (digit >= 'A' && digit <= 'F') {
        value += static_cast<std::uint32_t>(digit - 'A' + 10);
      } else {
        fail("invalid unicode escape");
      }
    }
    output.push_back(value <= 0x7fu ? static_cast<char>(value) : '?');
  }

  double parse_number() {
    const std::size_t start = position_;
    consume('-');
    if (position_ == text_.size()) {
      fail("incomplete number");
    }
    if (consume('0')) {
      if (position_ < text_.size() && text_[position_] >= '0' &&
          text_[position_] <= '9') {
        fail("leading zero");
      }
    } else {
      parse_digits();
    }
    if (consume('.')) {
      parse_digits();
    }
    if (consume('e') || consume('E')) {
      consume('+') || consume('-');
      parse_digits();
    }

    const std::string token(text_.substr(start, position_ - start));
    std::size_t parsed = 0u;
    const double result = std::stod(token, &parsed);
    if (parsed != token.size() || !std::isfinite(result)) {
      fail("invalid number");
    }
    return result;
  }

  void parse_digits() {
    const std::size_t start = position_;
    while (position_ < text_.size() && text_[position_] >= '0' &&
           text_[position_] <= '9') {
      ++position_;
    }
    if (position_ == start) {
      fail("expected digit");
    }
  }

  void parse_literal(std::string_view literal) {
    if (text_.substr(position_, literal.size()) != literal) {
      fail("invalid literal");
    }
    position_ += literal.size();
  }

  bool consume(char expected) {
    if (position_ < text_.size() && text_[position_] == expected) {
      ++position_;
      return true;
    }
    return false;
  }

  std::string_view text_;
  std::size_t position_ = 0u;
};

const JsonValue::Object& object(const JsonValue& value,
                                std::string_view path) {
  const auto* result = std::get_if<JsonValue::Object>(&value.storage);
  if (result == nullptr) {
    throw std::runtime_error(std::string(path) + " must be an object");
  }
  return *result;
}

const JsonValue::Array& array(const JsonValue& value, std::string_view path) {
  const auto* result = std::get_if<JsonValue::Array>(&value.storage);
  if (result == nullptr) {
    throw std::runtime_error(std::string(path) + " must be an array");
  }
  return *result;
}

const JsonValue& field(const JsonValue& value,
                       std::string_view name,
                       std::string_view path) {
  const auto& fields = object(value, path);
  const auto iterator = fields.find(name);
  if (iterator == fields.end()) {
    throw std::runtime_error(
        std::string(path) + " is missing field " + std::string(name));
  }
  return iterator->second;
}

const std::string& string_value(const JsonValue& value,
                                std::string_view path) {
  const auto* result = std::get_if<std::string>(&value.storage);
  if (result == nullptr || result->empty()) {
    throw std::runtime_error(
        std::string(path) + " must be a non-empty string");
  }
  return *result;
}

std::int64_t integer_value(const JsonValue& value, std::string_view path) {
  const auto* number = std::get_if<double>(&value.storage);
  if (number == nullptr || std::trunc(*number) != *number ||
      *number < static_cast<double>(std::numeric_limits<std::int64_t>::min()) ||
      *number > static_cast<double>(std::numeric_limits<std::int64_t>::max())) {
    throw std::runtime_error(std::string(path) + " must be an integer");
  }
  return static_cast<std::int64_t>(*number);
}

double number_value(const JsonValue& value, std::string_view path) {
  const auto* result = std::get_if<double>(&value.storage);
  if (result == nullptr) {
    throw std::runtime_error(std::string(path) + " must be a number");
  }
  return *result;
}

bool bool_value(const JsonValue& value, std::string_view path) {
  const auto* result = std::get_if<bool>(&value.storage);
  if (result == nullptr) {
    throw std::runtime_error(std::string(path) + " must be a boolean");
  }
  return *result;
}

std::vector<float> float_array(const JsonValue& value,
                               std::string_view path) {
  std::vector<float> result;
  const auto& values = array(value, path);
  result.reserve(values.size());
  for (const auto& element : values) {
    result.push_back(static_cast<float>(number_value(element, path)));
  }
  return result;
}

std::vector<std::uint8_t> byte_array(const JsonValue& value,
                                     std::string_view path) {
  std::vector<std::uint8_t> result;
  const auto& values = array(value, path);
  result.reserve(values.size());
  for (const auto& element : values) {
    const std::int64_t byte = integer_value(element, path);
    if (byte < 0 || byte > 255) {
      throw std::runtime_error(std::string(path) + " contains a non-byte");
    }
    result.push_back(static_cast<std::uint8_t>(byte));
  }
  return result;
}

std::string read_file(const std::string& path) {
  std::ifstream stream(path, std::ios::binary);
  if (!stream) {
    throw std::runtime_error("cannot open fixture " + path);
  }
  return std::string(
      std::istreambuf_iterator<char>(stream),
      std::istreambuf_iterator<char>());
}

JsonValue load_audio_fixture_text(std::string_view text,
                                  std::string_view expected_fixture_id) {
  JsonValue root = JsonParser(text).parse();
  if (integer_value(
          field(root, "contractVersion", "fixture"), "contractVersion") != 1) {
    throw std::runtime_error("fixture contractVersion must be 1");
  }
  if (string_value(field(root, "fixtureId", "fixture"), "fixtureId") !=
      expected_fixture_id) {
    throw std::runtime_error("fixtureId mismatch");
  }
  if (string_value(field(root, "category", "fixture"), "category") !=
      "audio") {
    throw std::runtime_error("fixture category must be audio");
  }

  const JsonValue& metadata = field(root, "metadata", "fixture");
  const JsonValue& local =
      field(metadata, "localNormalizedFormat", "metadata");
  const JsonValue& network = field(metadata, "networkFormat", "metadata");
  static_cast<void>(integer_value(
      field(local, "sampleRateHz", "localNormalizedFormat"),
      "localNormalizedFormat.sampleRateHz"));
  static_cast<void>(integer_value(
      field(local, "channels", "localNormalizedFormat"),
      "localNormalizedFormat.channels"));
  static_cast<void>(
      string_value(field(local, "sampleType", "localNormalizedFormat"),
                   "localNormalizedFormat.sampleType"));
  static_cast<void>(integer_value(
      field(network, "sampleRateHz", "networkFormat"),
      "networkFormat.sampleRateHz"));
  static_cast<void>(integer_value(
      field(network, "channels", "networkFormat"),
      "networkFormat.channels"));
  static_cast<void>(
      string_value(field(network, "sampleType", "networkFormat"),
                   "networkFormat.sampleType"));

  if (expected_fixture_id == "audio.pcm-conversion.v1") {
    const JsonValue& conversion =
        field(metadata, "conversion", "metadata");
    static_cast<void>(integer_value(
        field(conversion, "decoderFIRTaps", "conversion"),
        "conversion.decoderFIRTaps"));
  } else if (expected_fixture_id == "audio.pcm-batching.v1") {
    const JsonValue& batch = field(metadata, "networkBatch", "metadata");
    static_cast<void>(integer_value(
        field(batch, "byteCount", "networkBatch"),
        "networkBatch.byteCount"));
  }

  const auto& cases = array(field(root, "cases", "fixture"), "cases");
  if (cases.empty()) {
    throw std::runtime_error("fixture cases must not be empty");
  }
  std::map<std::string, bool, std::less<>> names;
  for (const auto& test_case : cases) {
    const std::string& name =
        string_value(field(test_case, "name", "case"), "case.name");
    static_cast<void>(
        string_value(field(test_case, "operation", "case"), "case.operation"));
    static_cast<void>(object(field(test_case, "input", "case"), "case.input"));
    if (!names.emplace(name, true).second) {
      throw std::runtime_error("duplicate fixture case " + name);
    }
  }
  return root;
}

JsonValue load_audio_fixture(std::string_view file_name,
                             std::string_view expected_fixture_id) {
  const std::string path =
      std::string(EMKE_AUDIO_PCM_FIXTURE_DIRECTORY) + "/" +
      std::string(file_name);
  return load_audio_fixture_text(read_file(path), expected_fixture_id);
}

const JsonValue& case_named(const JsonValue& fixture, std::string_view name) {
  for (const auto& test_case :
       array(field(fixture, "cases", "fixture"), "cases")) {
    if (string_value(field(test_case, "name", "case"), "case.name") == name) {
      return test_case;
    }
  }
  throw std::runtime_error("missing fixture case " + std::string(name));
}

template <typename Function>
bool throws_runtime_error(Function&& function) {
  try {
    function();
  } catch (const std::runtime_error&) {
    return true;
  }
  return false;
}

void test_fixture_loader_rejects_malformed_and_missing_fields(
    TestContext& context) {
  EXPECT(context,
         throws_runtime_error([] {
           static_cast<void>(load_audio_fixture_text(
               "{", "audio.pcm-conversion.v1"));
         }));
  EXPECT(context,
         throws_runtime_error([] {
           static_cast<void>(load_audio_fixture_text(
               R"({"contractVersion":1,"fixtureId":"audio.pcm-conversion.v1","category":"audio","metadata":{},"cases":[]})",
               "audio.pcm-conversion.v1"));
         }));
}

void test_fixture_inventory_and_format_constants(TestContext& context,
                                                 const JsonValue& batching,
                                                 const JsonValue& conversion) {
  EXPECT(context,
         array(field(batching, "cases", "batching"), "batching.cases").size() ==
             6u);
  EXPECT(context,
         array(field(conversion, "cases", "conversion"), "conversion.cases")
                 .size() == 7u);

  const JsonValue& conversion_metadata =
      field(conversion, "metadata", "conversion");
  const JsonValue& local = field(
      conversion_metadata, "localNormalizedFormat", "conversion.metadata");
  const JsonValue& network =
      field(conversion_metadata, "networkFormat", "conversion.metadata");
  const JsonValue& conversion_rules =
      field(conversion_metadata, "conversion", "conversion.metadata");
  EXPECT(context,
         integer_value(field(local, "sampleRateHz", "local"), "local.rate") ==
             emke::audio::localSampleRate);
  EXPECT(context,
         integer_value(field(local, "channels", "local"), "local.channels") ==
             emke::audio::localChannelCount);
  EXPECT(context,
         integer_value(
             field(network, "sampleRateHz", "network"), "network.rate") ==
             emke::audio::networkSampleRate);
  EXPECT(context,
         integer_value(
             field(network, "channels", "network"), "network.channels") ==
             emke::audio::networkChannelCount);
  EXPECT(context,
         integer_value(
             field(conversion_rules, "decoderFIRTaps", "conversion"),
             "conversion.decoderFIRTaps") == emke::audio::firTapCount);

  static_cast<void>(case_named(
      batching, "one exact network batch emits immediately"));
  static_cast<void>(case_named(
      batching, "two half batches combine into one network batch"));
  static_cast<void>(
      case_named(batching, "odd PCM16 append fails before buffering"));
  static_cast<void>(
      case_named(batching, "incomplete even tail remains buffered"));
  static_cast<void>(case_named(
      batching, "append larger than one batch retains the exact tail"));
  static_cast<void>(
      case_named(batching, "stop flush discards an incomplete tail"));
}

void expect_encoder_case(TestContext& context,
                         const JsonValue& test_case) {
  const JsonValue& input = field(test_case, "input", "encoder case");
  const JsonValue& expected = field(test_case, "expected", "encoder case");
  const auto local = float_array(
      field(input, "interleavedStereoFloat32", "encoder input"),
      "interleavedStereoFloat32");
  const auto expected_bytes = byte_array(
      field(expected, "pcm16LittleEndianBytes", "encoder expected"),
      "pcm16LittleEndianBytes");
  std::vector<std::uint8_t> output(expected_bytes.size(), 0u);
  emke::audio::PcmEncoder encoder;

  const auto result = encoder.process(local, output);

  EXPECT(context, result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, result.output_count == expected_bytes.size());
  EXPECT(context, output == expected_bytes);
}

void test_encoder_consumes_fixture_vectors(TestContext& context,
                                           const JsonValue& conversion) {
  expect_encoder_case(
      context,
      case_named(conversion, "encoder clamps Float32 endpoints exactly"));
  expect_encoder_case(
      context,
      case_named(
          conversion, "encoder downmixes stereo before averaging two frames"));
  expect_encoder_case(
      context,
      case_named(
          conversion,
          "encoder packs signed PCM16 in little endian byte order"));
}

void test_encoder_maps_non_finite_averages_deterministically(
    TestContext& context) {
  const float nan = std::numeric_limits<float>::quiet_NaN();
  const float positive_infinity = std::numeric_limits<float>::infinity();
  const float negative_infinity = -std::numeric_limits<float>::infinity();
  const std::vector<float> input = {
      nan,
      nan,
      0.0f,
      0.0f,
      positive_infinity,
      positive_infinity,
      1.0f,
      1.0f,
      negative_infinity,
      negative_infinity,
      -1.0f,
      -1.0f,
  };
  const std::vector<std::uint8_t> expected = {
      0x00u, 0x00u, 0xffu, 0x7fu, 0x00u, 0x80u};
  std::vector<std::uint8_t> output(expected.size(), 0xa5u);
  emke::audio::PcmEncoder encoder;

  EXPECT(context, std::feclearexcept(FE_ALL_EXCEPT) == 0);
  const auto result = encoder.process(input, output);
  const bool raised_invalid = (std::fetestexcept(FE_INVALID) != 0);
  EXPECT(context, std::feclearexcept(FE_ALL_EXCEPT) == 0);

  EXPECT(context, result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, result.output_count == expected.size());
  EXPECT(context, output == expected);
  EXPECT(context, !raised_invalid);
}

void test_encoder_odd_frame_chunks_match_contiguous_output(
    TestContext& context) {
  const std::vector<float> input = {
      0.0f, 0.0f, 0.5f, 0.5f, 1.0f, 1.0f,
      0.5f, 0.5f, -0.5f, -0.5f, -1.0f, -1.0f};
  const std::vector<std::uint8_t> expected = {
      0x00u, 0x20u, 0xffu, 0x5fu, 0x01u, 0xa0u};
  std::vector<std::uint8_t> contiguous_output(expected.size(), 0xa5u);
  std::vector<std::uint8_t> chunked_output(expected.size(), 0xa5u);

  emke::audio::PcmEncoder contiguous;
  const auto contiguous_result =
      contiguous.process(input, contiguous_output);

  emke::audio::PcmEncoder chunked;
  const auto first_result = chunked.process(
      std::span<const float>(input).subspan(0u, 2u),
      std::span<std::uint8_t>{});
  const auto second_result = chunked.process(
      std::span<const float>(input).subspan(2u, 6u),
      std::span<std::uint8_t>(chunked_output).subspan(0u, 4u));
  const auto third_result = chunked.process(
      std::span<const float>(input).subspan(8u, 4u),
      std::span<std::uint8_t>(chunked_output).subspan(4u, 2u));

  EXPECT(context,
         contiguous_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, contiguous_result.output_count == expected.size());
  EXPECT(context, first_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, first_result.output_count == 0u);
  EXPECT(context,
         second_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, second_result.output_count == 4u);
  EXPECT(context, third_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, third_result.output_count == 2u);
  EXPECT(context, contiguous_output == expected);
  EXPECT(context, chunked_output == expected);
  EXPECT(context, chunked_output == contiguous_output);
}

void test_encoder_insufficient_output_preserves_pending_frame_and_destination(
    TestContext& context) {
  const std::vector<float> first_frame = {0.0f, 0.0f};
  const std::vector<float> second_frame = {0.5f, 0.5f};
  const std::vector<float> contiguous_input = {0.0f, 0.0f, 0.5f, 0.5f};
  std::vector<std::uint8_t> insufficient_output(1u, 0xa5u);
  std::vector<std::uint8_t> retry_output(2u, 0xa5u);
  std::vector<std::uint8_t> fresh_output(2u, 0xa5u);

  emke::audio::PcmEncoder retry;
  const auto pending_result =
      retry.process(first_frame, std::span<std::uint8_t>{});
  const auto insufficient_result =
      retry.process(second_frame, insufficient_output);
  const auto retry_result = retry.process(second_frame, retry_output);

  emke::audio::PcmEncoder fresh;
  const auto fresh_result = fresh.process(contiguous_input, fresh_output);

  EXPECT(context, pending_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, pending_result.output_count == 0u);
  EXPECT(context,
         insufficient_result.status ==
             emke::audio::PcmConversionStatus::insufficientOutput);
  EXPECT(context, insufficient_result.output_count == 0u);
  EXPECT(context, insufficient_output == std::vector<std::uint8_t>{0xa5u});
  EXPECT(context, retry_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, fresh_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, retry_result.output_count == 2u);
  EXPECT(context, fresh_result.output_count == 2u);
  EXPECT(context, retry_output == fresh_output);
  EXPECT(context, retry_output == std::vector<std::uint8_t>({0x00u, 0x20u}));
}

void test_decoder_insufficient_output_preserves_history_and_destination(
    TestContext& context) {
  const std::vector<std::uint8_t> input = {
      0x00u, 0x20u, 0xffu, 0x5fu};
  std::vector<float> insufficient_output(7u, 9.0f);
  std::vector<float> retry_output(8u, 9.0f);
  std::vector<float> fresh_output(8u, 9.0f);

  emke::audio::PcmDecoder retry;
  const auto insufficient_result =
      retry.process(input, insufficient_output);
  const auto retry_result = retry.process(input, retry_output);

  emke::audio::PcmDecoder fresh;
  const auto fresh_result = fresh.process(input, fresh_output);

  EXPECT(context,
         insufficient_result.status ==
             emke::audio::PcmConversionStatus::insufficientOutput);
  EXPECT(context, insufficient_result.output_count == 0u);
  EXPECT(context,
         std::all_of(
             insufficient_output.begin(),
             insufficient_output.end(),
             [](float sample) { return sample == 9.0f; }));
  EXPECT(context, retry_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, fresh_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, retry_result.output_count == 8u);
  EXPECT(context, fresh_result.output_count == 8u);
  EXPECT(context, retry_output == fresh_output);
}

void test_decoder_frame_count_pairs_and_odd_input(TestContext& context,
                                                  const JsonValue& conversion) {
  const JsonValue& duplicate_case = case_named(
      conversion, "decoder duplicates each interpolated sample to left and right");
  const auto duplicate_input = byte_array(
      field(field(duplicate_case, "input", "duplicate case"),
            "pcm16LittleEndianBytes",
            "duplicate input"),
      "pcm16LittleEndianBytes");
  std::vector<float> output(duplicate_input.size() * 2u, 9.0f);
  emke::audio::PcmDecoder decoder;
  const auto result = decoder.process(duplicate_input, output);
  const JsonValue& duplicate_expected =
      field(duplicate_case, "expected", "duplicate case");

  EXPECT(context, result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context,
         result.output_count ==
             static_cast<std::size_t>(integer_value(
                 field(duplicate_expected, "outputSampleCount", "expected"),
                 "expected.outputSampleCount")));
  EXPECT(context,
         bool_value(
             field(duplicate_expected, "channelPairEquality", "expected"),
             "expected.channelPairEquality"));
  for (std::size_t index = 0u; index < result.output_count; index += 2u) {
    EXPECT(context, output[index] == output[index + 1u]);
  }

  const JsonValue& odd_case =
      case_named(conversion, "decoder rejects an odd PCM16 byte count");
  const auto odd_input = byte_array(
      field(
          field(odd_case, "input", "odd case"),
          "pcm16LittleEndianBytes",
          "odd input"),
      "pcm16LittleEndianBytes");
  std::vector<float> odd_output(4u, 7.0f);
  const auto odd_result = decoder.process(odd_input, odd_output);
  EXPECT(context,
         odd_result.status ==
             emke::audio::PcmConversionStatus::misalignedPcm16);
  EXPECT(context, odd_result.output_count == 0u);
}

void test_127_tap_blackman_streaming_interpolation(TestContext& context) {
  std::vector<std::uint8_t> impulse(128u, 0u);
  impulse[0] = 0xffu;
  impulse[1] = 0x7fu;
  std::vector<float> output(impulse.size() * 2u, 0.0f);
  emke::audio::PcmDecoder decoder;

  const auto result = decoder.process(impulse, output);
  const auto mono = [&output](std::size_t local_frame) {
    return output[local_frame * emke::audio::localChannelCount];
  };

  EXPECT(context, result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, result.output_count == 256u);
  EXPECT(context,
         std::fabs(mono(60u) - (-0.2102672905f)) <= 0.0000001f);
  EXPECT(context,
         std::fabs(mono(62u) - 0.6359710097f) <= 0.0000001f);
  EXPECT(context,
         std::fabs(mono(emke::audio::firGroupDelaySamplesAt48k) - 1.0f) <=
             0.0000001f);
  EXPECT(context,
         std::fabs(mono(64u) - 0.6359710097f) <= 0.0000001f);
  EXPECT(context,
         std::fabs(mono(66u) - (-0.2102672905f)) <= 0.0000001f);
  for (std::size_t frame = 0u; frame < result.output_count / 2u; ++frame) {
    EXPECT(context, output[frame * 2u] == output[frame * 2u + 1u]);
  }
}

void test_chunked_decode_matches_contiguous_fixture(TestContext& context,
                                                    const JsonValue& conversion) {
  const JsonValue& test_case = case_named(
      conversion, "chunked FIR decode matches contiguous decode across aligned chunks");
  const JsonValue& input = field(test_case, "input", "chunk case");
  const auto bytes = byte_array(
      field(input, "pcm16LittleEndianBytes", "chunk input"),
      "pcm16LittleEndianBytes");
  const auto& chunks =
      array(field(input, "alignedChunkByteCounts", "chunk input"),
            "alignedChunkByteCounts");
  const double tolerance = number_value(
      field(test_case, "tolerance", "chunk case"), "chunk tolerance");

  std::vector<float> contiguous_output(bytes.size() * 2u, 0.0f);
  emke::audio::PcmDecoder contiguous;
  const auto contiguous_result =
      contiguous.process(bytes, contiguous_output);

  std::vector<float> chunked_output(bytes.size() * 2u, 0.0f);
  emke::audio::PcmDecoder chunked;
  std::size_t input_offset = 0u;
  std::size_t output_offset = 0u;
  for (const auto& chunk_value : chunks) {
    const std::size_t chunk_size = static_cast<std::size_t>(
        integer_value(chunk_value, "alignedChunkByteCounts"));
    const auto result = chunked.process(
        std::span<const std::uint8_t>(bytes).subspan(input_offset, chunk_size),
        std::span<float>(chunked_output).subspan(output_offset));
    EXPECT(context, result.status == emke::audio::PcmConversionStatus::ok);
    input_offset += chunk_size;
    output_offset += result.output_count;
  }

  EXPECT(context,
         contiguous_result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, input_offset == bytes.size());
  EXPECT(context, output_offset == contiguous_result.output_count);
  for (std::size_t index = 0u; index < output_offset; ++index) {
    EXPECT(context,
           std::fabs(contiguous_output[index] - chunked_output[index]) <=
               tolerance);
  }
}

void test_400ms_block_produces_complete_local_output(
    TestContext& context,
    const JsonValue& batching) {
  const JsonValue& metadata = field(batching, "metadata", "batching");
  const JsonValue& network_batch =
      field(metadata, "networkBatch", "batching.metadata");
  const std::size_t network_batch_bytes = static_cast<std::size_t>(
      integer_value(
          field(network_batch, "byteCount", "networkBatch"),
          "networkBatch.byteCount"));
  const std::size_t translated_400ms_bytes = network_batch_bytes * 2u;
  std::vector<std::uint8_t> input(translated_400ms_bytes, 0u);
  std::vector<float> output(translated_400ms_bytes * 2u, 5.0f);
  emke::audio::PcmDecoder decoder;

  const auto result = decoder.process(input, output);

  EXPECT(context, translated_400ms_bytes == 19'200u);
  EXPECT(context, result.status == emke::audio::PcmConversionStatus::ok);
  EXPECT(context, result.output_count == 38'400u);
  EXPECT(context,
         result.output_count / emke::audio::localChannelCount == 19'200u);
  EXPECT(context,
         result.output_count / emke::audio::localChannelCount * 1'000u /
                 emke::audio::localSampleRate ==
             400u);
  EXPECT(context,
         std::all_of(
             output.begin(),
             output.end(),
             [](float sample) { return sample == 0.0f; }));
}

void test_decoder_reset_clears_fir_history(TestContext& context,
                                           const JsonValue& conversion) {
  const JsonValue& reset_case = case_named(
      conversion, "decoder FIR history resets only after explicit reset or stop");
  const JsonValue& input = field(reset_case, "input", "reset case");
  const auto warmup = byte_array(
      field(input, "warmupPCM16LittleEndianBytes", "reset input"),
      "warmupPCM16LittleEndianBytes");
  const auto probe = byte_array(
      field(input, "probePCM16LittleEndianBytes", "reset input"),
      "probePCM16LittleEndianBytes");
  std::vector<float> warmup_output(warmup.size() * 2u, 0.0f);
  std::vector<float> fresh_output(probe.size() * 2u, 0.0f);
  std::vector<float> warmed_output(probe.size() * 2u, 0.0f);
  std::vector<float> reset_output(probe.size() * 2u, 0.0f);
  std::vector<float> replacement_output(probe.size() * 2u, 0.0f);

  emke::audio::PcmDecoder fresh;
  EXPECT(context,
         fresh.process(probe, fresh_output).status ==
             emke::audio::PcmConversionStatus::ok);

  emke::audio::PcmDecoder decoder;
  EXPECT(context,
         decoder.process(warmup, warmup_output).status ==
             emke::audio::PcmConversionStatus::ok);
  EXPECT(context,
         decoder.process(probe, warmed_output).status ==
             emke::audio::PcmConversionStatus::ok);
  EXPECT(context, warmed_output != fresh_output);

  decoder.reset();
  EXPECT(context,
         decoder.process(probe, reset_output).status ==
             emke::audio::PcmConversionStatus::ok);
  EXPECT(context, reset_output == fresh_output);

  emke::audio::PcmDecoder replacement;
  EXPECT(context,
         replacement.process(probe, replacement_output).status ==
             emke::audio::PcmConversionStatus::ok);
  EXPECT(context, replacement_output == fresh_output);
}

}  // namespace

static_assert(emke::audio::networkSampleRate == 24'000u);
static_assert(emke::audio::localSampleRate == 48'000u);
static_assert(emke::audio::localChannelCount == 2u);
static_assert(emke::audio::networkChannelCount == 1u);
static_assert(emke::audio::firTapCount == 127u);
static_assert(emke::audio::firGroupDelaySamplesAt48k == 63u);
static_assert(emke::audio::localBlockFrames == 480u);
static_assert(
    noexcept(std::declval<emke::audio::PcmEncoder&>().process(
        std::declval<std::span<const float>>(),
        std::declval<std::span<std::uint8_t>>())));
static_assert(
    noexcept(std::declval<emke::audio::PcmDecoder&>().process(
        std::declval<std::span<const std::uint8_t>>(),
        std::declval<std::span<float>>())));

int run_pcm_converter_tests() {
  TestContext context;
  test_fixture_loader_rejects_malformed_and_missing_fields(context);
  try {
    const JsonValue batching =
        load_audio_fixture("pcm-batching.json", "audio.pcm-batching.v1");
    const JsonValue conversion =
        load_audio_fixture("pcm-conversion.json", "audio.pcm-conversion.v1");
    test_fixture_inventory_and_format_constants(context, batching, conversion);
    test_encoder_consumes_fixture_vectors(context, conversion);
    test_encoder_maps_non_finite_averages_deterministically(context);
    test_encoder_odd_frame_chunks_match_contiguous_output(context);
    test_encoder_insufficient_output_preserves_pending_frame_and_destination(
        context);
    test_decoder_insufficient_output_preserves_history_and_destination(
        context);
    test_decoder_frame_count_pairs_and_odd_input(context, conversion);
    test_127_tap_blackman_streaming_interpolation(context);
    test_chunked_decode_matches_contiguous_fixture(context, conversion);
    test_400ms_block_produces_complete_local_output(context, batching);
    test_decoder_reset_clears_fir_history(context, conversion);
  } catch (const std::exception& error) {
    std::cerr << "PCM fixture test setup failed: " << error.what() << '\n';
    return context.failures() + 1;
  }
  return context.failures();
}
