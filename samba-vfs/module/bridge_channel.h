#pragma once

#include <cstdlib>
#include <fstream>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>

#include <grpcpp/grpcpp.h>

namespace kaimo::control_plane {

inline std::string credential_path(const char* environment_name,
                                   const char* default_path) {
    const char* configured = std::getenv(environment_name);
    return configured && configured[0] != '\0' ? configured : default_path;
}

inline std::string read_credential(const std::string& path) {
    std::ifstream input(path, std::ios::in | std::ios::binary);
    if (!input)
        throw std::runtime_error("cannot open control-plane credential: " + path);

    std::ostringstream content;
    content << input.rdbuf();
    if (!input.good() && !input.eof())
        throw std::runtime_error("cannot read control-plane credential: " + path);

    std::string value = content.str();
    if (value.empty())
        throw std::runtime_error("empty control-plane credential: " + path);
    return value;
}

inline std::shared_ptr<grpc::Channel> create_mtls_channel(
    const std::string& address,
    const char* certificate_environment,
    const char* certificate_default,
    const char* key_environment,
    const char* key_default) {
    grpc::SslCredentialsOptions credentials;
    credentials.pem_root_certs = read_credential(credential_path(
        "KAIMO_BRIDGE_CA_CERT", "/run/secrets/kaimo-control-plane/ca.crt"));
    credentials.pem_cert_chain = read_credential(credential_path(
        certificate_environment, certificate_default));
    credentials.pem_private_key = read_credential(credential_path(
        key_environment, key_default));
    return grpc::CreateChannel(address, grpc::SslCredentials(credentials));
}

}  // namespace kaimo::control_plane
