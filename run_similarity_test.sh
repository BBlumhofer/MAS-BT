#!/bin/bash

# Test script for SimilarityAnalysisAgent
# This script sends a test message and checks the response

set -e

echo "╔════════════════════════════════════════════════════════════╗"
echo "║     SimilarityAnalysisAgent Test                           ║"
echo "╚════════════════════════════════════════════════════════════╝"
echo ""

cd "$(dirname "$0")"

# Check if Ollama is running
if ! curl -s http://localhost:11434/api/tags > /dev/null 2>&1; then
    echo "⚠️  WARNING: Ollama is not running on localhost:11434"
    echo "   Please start Ollama with: ollama serve"
    echo "   And pull the embedding model: ollama pull nomic-embed-text"
    echo ""
fi

# Run the tests with detailed output
echo "🧪 Running SimilarityAnalysisAgent Tests..."
echo ""

dotnet test --filter "FullyQualifiedName~SimilarityAnalysisTests" --logger "console;verbosity=detailed" 2>&1 | \
    grep -E "(Standard Output Messages|Passed|Failed|Total tests|╔|║|╚|📝|📊|📈|✅|⚠️|❌|Element|Similarity|Interpretation|═|→)" | \
    grep -v "xUnit.net"

TEST_RESULT=${PIPESTATUS[0]}

echo ""
if [ $TEST_RESULT -eq 0 ]; then
    echo "✅ All tests passed!"
else
    echo "❌ Some tests failed (exit code: $TEST_RESULT)"
fi

echo ""
echo "📋 Test Summary:"
echo "   - CalcEmbedding_WithTwoProperties: Tests Ollama embedding extraction"
echo "   - CalcCosineSimilarity_WithTwoEmbeddings: Tests cosine similarity calculation"
echo "   - SimilarityAnalysis_EndToEnd: Full workflow test"
echo "   - CalcEmbedding_WithWrongNumberOfElements: Validation test"
echo ""
echo "📄 Test Message: tests/TestFiles/similarity_request_assemble_screw.json"
echo "   Compares: 'Assemble' vs 'Screw'"
echo ""
echo "💡 Tip: For real Ollama results, ensure Ollama is running:"
echo "   ollama serve"
echo "   ollama pull nomic-embed-text"
echo ""

exit $TEST_RESULT
