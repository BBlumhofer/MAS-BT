#!/usr/bin/env python3
"""
Quick Similarity Test - Tests the similarity between two capabilities
Usage: ./quick_similarity_test.py [capability1] [capability2]
Example: ./quick_similarity_test.py Assemble Screw
"""
import sys
import requests
import math

def get_embedding(text, endpoint="http://localhost:11434", model="nomic-embed-text"):
    """Get embedding from Ollama"""
    try:
        response = requests.post(
            f'{endpoint}/api/embeddings',
            json={'model': model, 'prompt': text},
            timeout=30
        )
        response.raise_for_status()
        return response.json()['embedding']
    except Exception as e:
        print(f"❌ Error getting embedding for '{text}': {e}")
        return None

def cosine_similarity(vec1, vec2):
    """Calculate cosine similarity between two vectors"""
    dot_product = sum(a * b for a, b in zip(vec1, vec2))
    magnitude1 = math.sqrt(sum(a * a for a in vec1))
    magnitude2 = math.sqrt(sum(b * b for b in vec2))
    if magnitude1 == 0 or magnitude2 == 0:
        return 0.0
    return dot_product / (magnitude1 * magnitude2)

def interpret_similarity(similarity):
    """Interpret the similarity value"""
    if similarity >= 0.9:
        return "✅ Very High Similarity (nearly identical concepts)"
    elif similarity >= 0.7:
        return "✅ High Similarity (closely related concepts)"
    elif similarity >= 0.5:
        return "⚠️  Medium Similarity (somewhat related)"
    elif similarity >= 0.3:
        return "⚠️  Low Similarity (loosely related)"
    else:
        return "❌ Very Low Similarity (different concepts)"

def main():
    # Get capabilities from command line or use defaults
    if len(sys.argv) >= 3:
        cap1 = sys.argv[1]
        cap2 = sys.argv[2]
    else:
        cap1 = "Assemble"
        cap2 = "Screw"
    
    print("")
    print("╔══════════════════════════════════════════════════════════════╗")
    print("║           QUICK SIMILARITY TEST (Ollama)                    ║")
    print("╚══════════════════════════════════════════════════════════════╝")
    print("")
    
    # Check if Ollama is running
    try:
        requests.get("http://localhost:11434/api/tags", timeout=2)
    except:
        print("  ❌ ERROR: Ollama is not running!")
        print("     Start it with: ollama serve")
        print("")
        return 1
    
    print(f"  🔄 Computing similarity for:")
    print(f"     • '{cap1}' vs '{cap2}'")
    print("")
    
    # Get embeddings
    print("  🔄 Fetching embeddings from Ollama...")
    embedding1 = get_embedding(cap1)
    embedding2 = get_embedding(cap2)
    
    if embedding1 is None or embedding2 is None:
        print("  ❌ Failed to get embeddings")
        return 1
    
    print(f"  ✅ Embedding '{cap1}': {len(embedding1)} dimensions")
    print(f"  ✅ Embedding '{cap2}': {len(embedding2)} dimensions")
    print("")
    
    # Calculate similarity
    similarity = cosine_similarity(embedding1, embedding2)
    
    print("  📊 RESULTS:")
    print("")
    print(f"     Cosine Similarity: {similarity:.6f}")
    print(f"     → {similarity * 100:.2f}% similar")
    print("")
    print(f"     {interpret_similarity(similarity)}")
    print("")
    print("════════════════════════════════════════════════════════════════")
    print("")
    
    return 0

if __name__ == "__main__":
    sys.exit(main())
