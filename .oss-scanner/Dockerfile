# Build environment for Anthropic's OSS Scanner (https://red.anthropic.com/oss-scanner/).
# The scanner builds this with the repository root as the build context and runs its agent with no
# network afterwards, so every NuGet package must be restored here.
#
# Only the C# projects are built. The two .esproj SPAs (Presentation.WebUI, Presentation.Dashboard)
# are TypeScript front ends that never run server-side; their source is present under /src for
# review but is not compiled, which keeps Node and npm out of the image.
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# scanner contract: the checkout lives inside the image, at /src
COPY . /src
WORKDIR /src/src

# Build every C# project (hosts, libraries and test projects) as the maintainers' CI does, in Release.
# Release is deliberate: it drops the Debug-only .esproj references and the SpaProxy package, so no
# npm step runs. A failing project fails the image build, and primary_contact gets the log.
RUN set -e; \
    for proj in $(find Content -name '*.csproj' | sort); do \
      dotnet restore "$proj"; \
    done; \
    for proj in $(find Content -name '*.csproj' | sort); do \
      dotnet build "$proj" -c Release --no-restore -p:TreatWarningsAsErrors=false; \
    done

# Tests are not run here. Part of the suite needs Postgres and Docker (see .github/workflows/ci.yml),
# which an offline agent does not have. The built test assemblies under Content/Tests/*/bin/Release
# are left in place for anything worth exercising.
