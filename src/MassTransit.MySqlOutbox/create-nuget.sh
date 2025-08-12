dotnet build -c release
dotnet pack -c release -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg