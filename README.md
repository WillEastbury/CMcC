# CMcC
An example of creating an interactive agent that can consume / ground in multiple live data sources based upon configuration.

## Idea
The agent should simply have some grounding from an API before the chat context even starts - this makes the need for that data and the prompt to be hyper-tuned and the framework here should show that. 

LLM API -> Context starts by generating parameters for the query. 
Our example will be a horse racing prediction engine based on the BetFair apis for odds and results data.
We will need to determine the day, any races, and the racecard data from BetFair, current and predicted changes in weather at those locations that may effect the going of the ground.

Then we should attribute prices to each horse without considering the current odds and look for undervalued picks based on a set of rules in the prompt.
