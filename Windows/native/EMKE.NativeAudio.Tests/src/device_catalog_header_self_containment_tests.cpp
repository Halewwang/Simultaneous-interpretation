#include "device_catalog.hpp"

int main() {
  emke::audio::EndpointDiscoveryResult result;
  return result.virtual_endpoints.size() == 0u
             ? 1
             : 0;
}
